## Binary rewriting for systematic testing

To enable systematic testing of unmodified programs, InterleaveX performs _binary rewriting_ of
managed .NET assemblies. This process loads one or more of your assemblies (`*.dll`, `*.exe`) and
rewrites them for systematic testing (for production just use the original unmodified assemblies).
The rewritten code maintains exact semantics with the production version (so you don't need to worry
about false bugs), but has stubs and hooks injected that allow InterleaveX to take control of
concurrent execution and various sources of nondeterminism in a program.

To invoke the rewriter use the following command:

```plain
interleavex rewrite ${PATH}
```

`${PATH}` is the path to the assembly (`*.dll`, `*.exe`) to rewrite or to a [JSON rewriting
configuration file](#configuration) (`*.json`). For automation, this can be conveniently done in a
post-build task, like this:
```xml
<Target Name="InterleaveXRewrite" AfterTargets="AfterBuild">
  <Exec Command="dotnet $(PathToInterleaveX)/interleavex.dll rewrite ${PATH}"/>
</Target>
```

To learn how to test your application after rewriting your binaries with InterleaveX, read
[here](../get-started/using-coyote.md), as well as check out our tutorial on [writing your first
concurrency unit test](../tutorials/first-concurrency-unit-test.md).

### Configuration

If you have multiple binaries to rewrite, then you should provide a JSON rewriting configuration
file, which looks like this example:

```json
{
  "AssembliesPath": "bin/net8.0",
  "OutputPath": "bin/net8.0/rewritten",
  "Assemblies": [
    "BoundedBuffer.dll",
    "MyOtherLibrary.dll",
    "FooBar123.dll"
  ]
}
```

- `AssembliesPath` is the folder containing the original binaries.  This property is required.

- `OutputPath` allows you to specify a different location for the rewritten assemblies. The
`OutputPath` can be omitted in which case it is assumed to be the same as `AssembliesPath` and in
that case the original assemblies will be replaced.

- `Assemblies` is the list of specific assemblies in `AssembliesPath` to be rewritten. You must
  explicitly list all the assemblies to rewrite (pattern matching, `*` and `.` are not supported).

Paths may contain the `$(TargetFramework)` and `$(Configuration)` tokens, which are resolved from
the assembly that invokes the rewriter. Prefer them over a hard-coded path such as `bin/net8.0`
when a project emits more than one target framework or configuration, since that path is otherwise
ambiguous as to which build it refers to:

```json
{
  "AssembliesPath": "bin/$(Configuration)/$(TargetFramework)",
  "Assemblies": [ "BoundedBuffer.dll" ]
}
```

Then pass this JSON file on the command line: `interleavex rewrite config.json`.

### Resolving dependencies

Rewriting reads the assemblies that the rewritten code references, because what it emits depends on
what those references contain. They are looked for beside the assembly being rewritten, then in the
directories given below, then in the shared frameworks installed on the machine.

If the assemblies you rewrite do not sit beside the assemblies they reference, name the directories
to search:

```plain
interleavex rewrite ${PATH} --dependency-search-path bin/$(Configuration)/$(TargetFramework)
```

Repeat the option to give more than one. A path given on the command line is relative to the
directory you run the command from. The equivalent in a JSON configuration file, where it is
relative to the configuration file instead, is:

```json
{
  "AssembliesPath": "bin/$(Configuration)/$(TargetFramework)",
  "DependencySearchPaths": [ "../OtherProject/bin/$(Configuration)/$(TargetFramework)" ],
  "Assemblies": [ "BoundedBuffer.dll" ]
}
```

Point these at implementation assemblies rather than at reference assemblies. Reference assemblies
carry no method bodies, so resolving against them changes what the rewriter can see and therefore
what it emits.

### Incremental rewriting

Rewriting an assembly of any size takes seconds, so it is skipped when it has already been done.
Each run records what it read and wrote in a `rewriting.cache.json` file in the output directory,
and a later run that finds all of it unchanged reports that everything is up to date and stops:

```plain
... Skipping rewriting as every assembly is up to date
```

The record covers everything that changes what rewriting produces: the content of the assemblies
being rewritten and of every assembly resolved while rewriting them, the rewritten output itself,
whether symbol files are present, which referenced assemblies are present beside the input, the
rewriting options, and the build of the rewriter. Change any of them and the work runs again.
Content is compared by hash, so restoring a file or otherwise moving its timestamp does not count as
a change, and editing it does even if the timestamp is preserved.

To rewrite regardless, pass `--no-incremental`, or set `INTERLEAVEX_NO_REWRITE_CACHE=1` to disable
the cache for every invocation that sees it, which is easier to apply to a build that invokes the
rewriter for you. Either way the record is still updated, so the run after it can be skipped again.
Deleting the `rewriting.cache.json` file has the same effect once.

Nothing about the cache can fail a run: a record that cannot be read, or that does not describe the
run being attempted, means the work happens rather than that it is skipped.

None of this reaches an assembly that was rewritten in place, because such an assembly records the
run in itself rather than in the cache. Rewriting is not idempotent, so it is skipped when its own
record matches, and the run fails when that record was written by a different build of the rewriter
or under different options — there is no longer an original to rewrite. Build the project again, or
delete the assembly and build again, and rewrite that. The section below avoids the situation
altogether by rewriting a staged copy of the compiler's output.

#### Rewriting from a build

The post-build task shown [above](#binary-rewriting-for-systematic-testing) rewrites the assembly in
the output directory, which cannot be skipped by the build: MSBuild copies the compiled assembly
over it every time, so the instrumentation has to be applied again on every build.

To let the build skip it, rewrite a copy of the compiled assembly instead, and copy the result into
the output directory afterwards:

```xml
<Target Name="InterleaveXRewriteFingerprint" DependsOnTargets="ResolveReferences">
  <ItemGroup>
    <RewriteFingerprintLine Include="@(RewriterAssembly->'%(FullPath)|%(ModifiedTime)')" />
    <RewriteFingerprintLine Include="$(RewriteArgs)" />
  </ItemGroup>
  <WriteLinesToFile File="$(IntermediateOutputPath)interleavex.fingerprint"
                    Lines="@(RewriteFingerprintLine)" Overwrite="true" WriteOnlyWhenDifferent="true" />
</Target>
<Target Name="InterleaveXRewrite" BeforeTargets="CopyFilesToOutputDirectory"
        DependsOnTargets="ResolveReferences;InterleaveXRewriteFingerprint"
        Inputs="@(IntermediateAssembly);@(ReferencePath);$(IntermediateOutputPath)interleavex.fingerprint"
        Outputs="$(IntermediateOutputPath)interleavex\$(TargetFileName)">
  <ItemGroup>
    <RewriteSearchDirRaw Include="@(ReferenceCopyLocalPaths->'%(RootDir)%(Directory)'->Distinct())"
                         Condition="'%(Extension)'=='.dll'" />
    <RewriteSearchDir Include="@(RewriteSearchDirRaw)">
      <Path>$([System.IO.Path]::GetDirectoryName('%(Identity)'))</Path>
    </RewriteSearchDir>
  </ItemGroup>
  <PropertyGroup>
    <RewriteStageDir>$(IntermediateOutputPath)interleavex\</RewriteStageDir>
    <RewriteSearchArgs>@(RewriteSearchDir->'--dependency-search-path "%(Path)"',' ')</RewriteSearchArgs>
  </PropertyGroup>
  <RemoveDir Directories="$(RewriteStageDir)" />
  <Copy SourceFiles="@(IntermediateAssembly);@(_DebugSymbolsIntermediatePath)"
        DestinationFolder="$(RewriteStageDir)" />
  <Exec Command="dotnet $(PathToInterleaveX)/interleavex.dll rewrite $(RewriteStageDir)$(TargetFileName) $(RewriteSearchArgs) $(RewriteArgs)" />
</Target>
<Target Name="InterleaveXCopyRewritten" AfterTargets="CopyFilesToOutputDirectory"
        DependsOnTargets="InterleaveXRewrite">
  <Copy SourceFiles="$(IntermediateOutputPath)interleavex\$(TargetFileName)" DestinationFolder="$(OutDir)" />
</Target>
```

The copy sits on its own, away from the references it was compiled against, so the search paths are
what let the rewriter resolve them. They are taken from the assemblies the build copies next to the
output rather than from `@(ReferencePath)`, which also names reference assemblies. The trailing
separator is stripped from each one because they are passed as quoted arguments, and on Windows a
backslash immediately before the closing quote escapes it.

Set `RewriterAssembly` to the rewriter's own assemblies and `RewriteArgs` to whatever extra arguments
you pass it. Neither reaches `Inputs` on its own, and both change what rewriting produces, so without
the fingerprint the target is skipped when either changes and the previous instrumentation is kept.
The staging directory is emptied rather than written over, because the rewriter copies its runtime
assemblies in beside the staged one and preserves matching versions it finds already there, and this
directory is searched first.

Rewriting a copy also keeps the compiler's output as the compiler left it, which matters because
rewriting is not idempotent. An assembly that has already been rewritten is skipped rather than
rewritten again, so a build that rewrote the compiled assembly in place would read its own output on
the next run and quietly keep whatever instrumentation was already in it.

### Which DLLs to rewrite?

**TLDR:** The short answer (and our recommendation) is that ideally you should just rewrite your
test DLLs, as well as your production code DLLs (which means the code that you and your team owns),
and to not rewrite any external dependencies (which you assume are correct after all).

The reason behind this recommendation is that there are certain trade-offs when rewriting DLLs
because of two issues: InterleaveX today does not support every single concurrency API in C#
(instead mostly focuses on the popular [task-asynchronous programming
model](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/concepts/async/)); and
dealing with the infamous state (schedule) space explosion problem.

Regarding the 1st issue, InterleaveX is focused on [asynchronous
task-based](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/concepts/async/)
concurrency (basically common things like `Task` objects and `async`/`await`). So if an external
library (or some "low-level" dependency DLL) is written with "lower-level" threading APIs (such as
explicitly spawning threads and waiting on synchronization primitives such as a `WaitHandle`) or
uses custom concurrency semantics (for example via a custom `TaskScheduler` or custom threadpools),
and you decide to rewrite these DLLs, then InterleaveX will either (a) not be able to intercept
these concurrency mechanisms properly (if the C# APIs is not supported by InterleaveX yet) which can
end up regressing exploration, or (b) be able to intercept them but the state (schedule) space in
your test will explode (more on this below). The good news is that using these "low-level" APIs is
uncommon in _most_ user applications/services, but of course some frameworks/library dependencies do
use them.

Regarding the 2nd issue, the more concurrent code you instrument, the more scheduling decisions
InterleaveX must explore in every test iteration. This _exponentially_ increases how much time you
need to test to cover the same code surface of your application. This is known as state space
explosion. Since InterleaveX explores under a test "budget" (such as number of test iterations) the
bigger the state space to explore, the less efficient InterleaveX will be. Ideally, you just want to
focus on testing your own concurrent code, and not the code of 3rd party frameworks/libraries (which
you assume is correct!). For this reason, its recommended instead of rewriting every single
dependency, to just rewrite DLLs that you (and your team) owns. This basically means to focus
rewriting the test DLL as well as your production code DLLs, assuming these DLLs only use tasks,
`async`/`await` and these kind of "high-level" concurrency primitives. Think about this as
"component-wise" testing.

Under the hood, InterleaveX deals with both of the above problems using a feature called
_partially-controlled exploration_. In this mode, which is enabled by default when testing a
partially-rewritten program (rewritten DLLs you own, and un-rewritten 3rd party DLLs), InterleaveX
will treat any un-rewritten DLLs as "pass-through", and their methods are invoked _atomically_. This
means that while InterleaveX sequentializes the program execution to explore different execution
paths and scheduling decisions (see [here](concurrency-unit-testing.md)), if it encounters a call to
an un-rewritten method (or unsupported C# system API), instead of giving up, or immediately
scheduling something else (resulting in lost coverage), InterleaveX will instead have a chance to
wait for the uncontrolled call to complete (with some tunable time bound, which is a heuristic
inside InterleaveX). This means that coverage wont regress in most cases.

Ideally, you want to mock important external dependencies (for example storage backends such as
CosmosDB) with some in-memory mock implementation to make your test fast and efficient, but at least
you can already get tests up and running without requiring to mock every single thing, making the
experience pay-as-you-go. And our plan is that as partially-controlled exploration improves over
time, you transparently also get better coverage without having to do much from your side.

### Quality of life improvements through rewriting

InterleaveX will automatically rewrite certain parts of your test code (without changing the
application semantics) to improve the testing experience. For example:

During testing InterleaveX needs to be able to terminate a test iteration at any time in order to
support the `--max-steps` command line argument. This termination is done using a special
InterleaveX `ExecutionCancelledException`. The problem is when your code contains one of the
following:

```csharp
} catch {
} catch (Exception) {
} catch (RuntimeException) {
```

These will inadvertently catch the special InterleaveX exception, which then stops `--max-steps`
from working. The recommended fix is to add a `when (!(e is Microsoft.Coyote.RuntimeException))`
filter. The good news is that `interleavex rewrite` can take care of this for you automatically so
you do not need to modify any of your exception handlers.

### Supported rewriting targets

InterleaveX binary rewriting intercepts the following concurrency constructs:

- **Task-based concurrency**: `Task`, `Task<TResult>`, `ValueTask`, `ValueTask<TResult>`,
  `TaskCompletionSource<TResult>`, and the `async`/`await` keywords.
- **Task combinators**: `Task.WhenAll`, `Task.WhenAny`, `Task.WhenEach` (introduced in .NET 9),
  including `ReadOnlySpan`-based overloads.
- **Explicit task construction**: The `new Task(() => ...) + task.Start()` pattern, including
  `Task.RunSynchronously`.
- **Synchronization primitives**: The `lock` keyword, `Monitor.Enter`/`Exit`/`TryEnter`/`Wait`/
  `Pulse`/`PulseAll`, and the `System.Threading.Lock` type (introduced in .NET 9) including
  `EnterScope`, `Enter`, `Exit`, `TryEnter`, and `IsHeldByCurrentThread`.
