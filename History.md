## InterleaveX fork (2026-05-10 onward)

InterleaveX is a fork of Microsoft Coyote, beginning at upstream version
1.7.11. See [docs/overview/fork-rationale.md](docs/overview/fork-rationale.md)
for the fork's purpose and maintenance scope. Entries below the InterleaveX
section in this file are upstream Microsoft Coyote release notes, retained for
historical reference.

This fork is maintained by TradingFuturo, LLC (https://pipflow.com). It was
originally developed for internal use and subsequently published in response to
community interest. We maintain a suite of 200+ Coyote-based tests and target
Microsoft .NET 10 in the development of the PipFlow Platform®, our AI-driven
order-flow trading platform, where InterleaveX helps us detect and minimize
concurrency defects across our codebase.

PipFlow Platform® is a registered trademark of TradingFuturo, LLC.

### v1.8.0 (InterleaveX)
- Whether the output directory folds case is read from that directory rather than
  inferred from one above it. Windows keeps case sensitivity per directory, so an
  insensitive parent can hold a sensitive child, and the probe flipped the case of
  a directory's own name and asked its parent to resolve it — a question about the
  parent's entries, never about the directory the answer is used for. The answer
  picks the comparer that decides whether a rewritten output is recognised as
  protected before the original is copied over it, and the wrong one there leaves
  an uninstrumented output that nothing downstream detects. The flag is now read
  directly on Windows; everywhere else case folding belongs to the mounted file
  system, which the enclosing directory does answer for, and the probe remains.
- A directory offered to resolution is identified by what is in it, not only by
  how big it is. Each assembly was recorded by name and length, on the reasoning
  that anything both offered and read is already recorded with its hash — which
  leaves out exactly the assemblies that were *not* read. One that failed to
  resolve, replaced by a different assembly of the same length, changed nothing
  the run looked at, and the whole rewrite was skipped although resolution would
  now succeed and produce different IL. The input directory and the configured
  search paths are now hashed. The shared frameworks keep the cheap form, since
  reading several hundred assemblies on both the check and the write path would
  cost more than the rewrite being skipped, but gain the write time.
- The copy into the output directory no longer skips input subtrees whose names
  merely begin like the output's. It avoids copying the output into itself by
  testing whether a directory's path starts with the output's, so an output of
  'bin/out' also claimed 'bin/output-assets' and left everything under it out of
  the mirror entirely. The test is now by path segment, and under the file
  system's own case rules rather than ordinal.
- Every test that builds its own testing engine is seeded like the rest. The
  per-test seed is applied where the base class builds the engine, which covers
  no test that builds one itself; one such test ran a real engine from a fresh
  seed on every run, so a failure it found could not be reproduced. The methods
  that build their own engine are now frozen in a list per assembly, and a new
  one fails. Because a test can only read the assemblies beside it — a test
  project's output holds its own and nothing of its siblings — a project that
  froze nothing was checked by nothing. A semantic build analyzer now requires
  the guard, and a centralized IL test verifies the compiled assemblies too.
- A run asked to explore from a new seed each time now writes that seed down. It
  was left for the runtime to derive from a fresh guid, which explored just as
  widely but put the value only inside the strategy description — so the nightly
  job's instructions for reproducing a failure pointed at a sentence that run
  never printed. The seed is drawn once per test and reported in the same words
  in both modes, which is the sentence those instructions name.
- The cache of which directories fold case is keyed ordinally. Folding case in
  that key filed `Foo` and `foo` together, and where a case-sensitive Windows
  parent holds both, each carries its own flag and whichever was probed first
  answered for the other — picking the comparer that decides whether a rewritten
  output is recognised before the original is copied over it. Two probes for one
  directory is the cost, and it is the cheaper mistake.
- The copy into the output directory refuses to read a file something else is
  writing, rather than comparing whatever bytes are there at the moment. It skips
  a copy when the destination already holds what copying would put there, decided
  on content — and a file caught half way through being written can hold exactly
  the bytes already in the output, at which point equal is the answer that leaves
  the stale ones in place. Reading and comparing want opposite things here and
  each must not have the other's: a hash taken mid-write matches nothing, which is
  read as "changed" and costs a rewrite, while the same read on the comparison
  path costs an uninstrumented output that nothing downstream detects. Which of
  the two a read asks for is now part of asking for it.
- `run-tests.ps1` tells you to build first only when something was never built.
  The advice was printed after any failing run, including an ordinary failing
  test, which sends whoever is reading it somewhere the failure is not. The two
  cases were already told apart to report them; that distinction now reaches the
  advice as well.
- The script helper checks no longer depend on what happens to be built. One of
  them drives `run-tests.ps1` to prove that a run with nothing to do fails, and it
  did so against this repository — where before the build there is nothing at all,
  which is a different case than the one it asserted, and after a Windows CI build
  there is `net462`, at which point asking for `net462` does not test nothing but
  runs the whole Tools suite and passes. It now runs against a copy of the scripts
  laid beside a fake tree, which fixes the answer, checks both ways of contributing
  no test run, and cannot run a test whatever happens.
- Exploration is faster and allocates far less per scheduling step. Measured with
  `Tools/SchedulerBench` at 100 iterations on an otherwise idle machine, against
  the three changes below combined: the `deep` workload, which isolates per-step
  cost, runs 16.9% faster and allocates 45.2% less; `wide`, which isolates cost
  that scales with the number of operations created, runs 14.2% faster and
  allocates 67.2% less. Step counts are identical in both, so the comparison is
  of the same work. The allocation reductions come from four sources, all on
  paths that run at every step. `OperationGroup.IsCompleted` used LINQ `All`, which
  takes an `IEnumerable` and so boxes the set's struct enumerator; the
  prioritization and delay-bounding strategies evaluate it for every group they
  track at every step, so a test with two hundred operations boxed two hundred
  enumerators per step, and this alone accounts for most of the `wide` result.
  Q-learning built its cumulative distribution through a LINQ projection that
  closed over a running total, allocating a closure, a delegate, an iterator and
  a list per decision, and allocated a fresh list for each boolean and integer
  choice besides; these are now a prefix sum computed in place and a reusable
  buffer. Its execution path was a linked list, allocating a node per step, a
  hundred thousand of them per iteration at the default fair bound. And
  `ExecutionTrace.Step` carried `Previous` and `Next` fields that were written on
  every push and read nowhere.
- Debug log arguments are no longer boxed only to be discarded. The
  `object`-typed `LogDebug` overloads box at the call site, and the verbosity
  check happens two frames further in, so with debug logging off — the default —
  every scheduling step boxed several thread ids, scheduling point kinds and
  runtime identifiers for the callee to drop. The four-argument call in
  `ScheduleNextOperation` also allocated a `params` array. Generic overloads now
  check the level first; overload resolution prefers them without any call site
  changing. `LogWriterAllocationTests` pins the property by asserting that
  allocation does not grow with the call count, since nothing observable changes
  if boxing returns.
- The program state is computed only for the strategies that read it. The
  scheduler enabled implicit program-state hashing for the whole run whenever
  portfolio mode was on, which is the default, and computing that state walks
  every registered operation and every specification monitor at every scheduling
  point and every nondeterministic choice. Q-learning is the only strategy that
  reads the result and is one of the five the portfolio rotates through, so four
  iterations in five paid for a value nothing consumed. A strategy now declares
  whether it needs the state and the scheduler caches the answer per iteration.
  Scheduler setup also no longer writes to the caller's configuration, which had
  turned a per-iteration decision into a run-wide, caller-visible mutation.
  Note that only the implicit operation and monitor contribution is gated:
  state-hashing functions registered through `Specification` are the user
  computing state for their own purposes and still run on every iteration. The
  visited-state count reported for portfolio runs is correspondingly lower, since
  only the iterations that compute the state contribute to it.
- The determinism goldens record the explored traces separately from the
  statistics they were previously fused with, so a mismatch distinguishes
  "exploration changed" from "a reported number changed". All three changes above
  leave the trace digests of all 39 swept configurations byte-identical.
- `Tools/SchedulerBench` gained `--force-hashing`, which restores the pre-gating
  behaviour so both configurations can be measured from one build. Comparing two
  builds attributes everything that differs between them to whichever change is
  under test, and that cross-tree noise proved larger than the effect being
  measured.
- The build output layout check no longer reports its own examples. It excuses a
  line by quoting it verbatim, so its own table of exceptions, its description
  and the message it prints on failure all read as stale references the moment
  the file itself was tracked — fourteen of them, in the checker that exists to
  find them. It now skips itself, and reports any exception that has stopped
  matching a line, which is the drift the text matching was there to catch.
  `check-script-helpers.ps1` asserts that every file it exempts is still tracked.
- Benchmark history reaches the fork point again. The rewrite step of
  `BenchmarkRunner.csproj` runs the CLI out of the configuration-specific build
  output, and that project is restored over every commit measured, so the 34
  commits older than "Separate debug and release build output" — where the CLI
  emitted to a directory that did not name a configuration — failed to build
  instead of being measured. The step now resolves the CLI when it runs and
  accepts either layout, which is what makes those commits comparable at all.
- A failed restore is no longer reported as a successful run.
  `run-benchmark-history.ps1` checks out each commit in turn and puts the
  repository back in a `finally` block, but neither the reset nor the checkout
  was checked. A locked file or a checkout that could not proceed left the caller
  detached on whichever commit was last measured, while the script printed its
  results path in green and exited 0. Both are checked now, the recovery command
  is printed, and the run fails.
- An unmeasurable commit range is caught before the first build rather than at
  the end of the run. The range came from a `git log` whose exit code was never
  read: a fork point that no longer exists left an empty history that read as
  zero work and reported success, and a fork point on another branch does not
  fail `git log` at all — it silently yields everything reachable from HEAD,
  reaching back past the rebrand where the restored sources cannot build. The
  fork point is now required to exist and to be an ancestor of HEAD, and an
  empty range is an error.
- The NuGet packages are built in `Release` only, and `build.ps1` says so instead
  of producing packages nothing can restore. Packing emits into a
  configuration-specific directory while the `local` feed names exactly one, and
  package source mapping routes `InterleaveX*` to that feed alone, so a Debug
  pack left the samples unable to find any package at all — the same failure the
  feed path was corrected for below, arriving by a different route.
- A failing benchmark run now fails the script. Both benchmark scripts ignored
  the runner's exit code, and the history script's only check was that the
  output directory existed — which proves nothing, because the runner creates
  that directory while parsing its arguments, before a single benchmark runs.
  A filter that matched nothing, a failed benchmark build and any unhandled
  exception all reported success. The exit code is now propagated and the
  results themselves are checked.
- `run-tests.ps1` no longer reports success after running no tests. A target
  whose configuration was never built produced only a non-terminating error, and
  a `-framework` that named an output nobody built was filtered out in silence;
  either way the run reached the end and exited 0. Each selected target must now
  contribute a test run, and the two causes are reported separately.
- Historical benchmark runs are no longer contaminated by the tree they start
  from. The snapshot held constant across commits was taken with a recursive
  copy, so it carried `bin` and `obj` into every commit measured, where a build
  can reuse them instead of rebuilding; only the files git tracks are copied
  now. The snapshot also omitted `Tests/Tests.Actors.Performance`, leaving six
  of the eight benchmarks commit-specific and able to read as runtime
  regressions.
- Both benchmark scripts run on any platform. They invoked
  `BenchmarkRunner.exe`, which exists only on Windows, and the history script
  used `$ENV:TEMP`, which is unset elsewhere and would have placed its scratch
  and output directories inside the repository it resets. The runner is now
  invoked through the dotnet host.
- Uploading benchmark results is opt-in. `run-benchmark-history.ps1` passed
  `-cosmos` unconditionally, which made the runner read and parse the git log
  once per commit and would have uploaded to the shared database whenever the
  credentials happened to be present. `run-benchmarks.ps1` tested
  `$env:AZURE_COSMOSDB_ENDPOINT -ne ""`, which is true when the variable is
  unset, so it always asked for an upload it usually could not perform; it now
  requires both credentials.
- An output directory beside the repository is no longer rejected as being
  inside it. The check compared raw prefixes, so `coyote-results` looked like
  part of `coyote`. Added `Scripts/check-script-helpers.ps1`, run by CI and by
  `run-tests.ps1`, which asserts this and the other decisions the build and
  benchmark scripts share.
- Cancelling a channel wait now reports the caller's token whether the
  cancellation arrives before the call or after the waiter parks. A parked
  waiter's completion source recorded the token, but the awaiter state machine
  that surfaces it to the caller completed with a parameterless `SetCanceled`,
  which records none; the resulting `OperationCanceledException` carried
  `CancellationToken.None`, unlike the pre-canceled path, which returns
  `Task.FromCanceled(token)`. Callers that compare the token, or filter a catch
  on it, silently stopped matching once the cancellation raced the wait.
- A worker process of a parallel run that fails after producing its results no
  longer passes as a successful run. Workers saved their report before emitting
  trace and coverage artifacts, and the coordinator consulted only whether that
  report existed, so a worker that exited with an error after writing a clean
  report was merged and the run reported success. The report is now saved last,
  once everything that can fail has run, and the coordinator reports any worker
  that exits with an unexpected code as an internal error of the merged run,
  along with that worker's output.
- The local NuGet feed, the sample rewrite steps and the documented install
  command follow the configuration-specific build layout introduced below.
  Packages moved to `bin/Release/nuget` but `NuGet.config` still pointed the
  `local` feed at `bin/nuget`, and package source mapping routes `InterleaveX*`
  exclusively to that feed, so restoring the samples against a local build
  failed to find any package at all. Added `Scripts/check-build-layout.ps1`,
  run by CI and by `run-tests.ps1`, which reports any reference to the product
  output that does not name a configuration.
- CI artifacts are named after the platform that produced them. All three
  matrix legs uploaded under one name, which is a conflict rather than a merge,
  and the packages the samples job needs are now published once from the leg
  that produces them.
- The IL-diff golden hashes are rebaselined. They had been stale since
  "Redirect every producer of a configured awaitable" changed the IL injected
  into `Tests.BugFinding` without regenerating them, and the rewriter work that
  followed moved the rest, so the validation job had been failing on four
  projects that no recent change had touched.
- `run-benchmark-history.ps1` works again. It copied test directories renamed
  long ago and shelled out to `sed`, so it stopped before reaching the
  benchmark runner. It now builds the benchmark runner project directly rather
  than rewriting the solution file, is restricted to the fork's own history,
  refuses to run against a dirty working tree, keeps its copies and results
  outside the repository, and restores the branch it started from.
- **Breaking (command line):** `--parallel` no longer takes a value. It is now a
  flag that uses one worker per logical processor, and the count moved to a new
  `--workers N` option that requires it, so `--parallel 8` becomes
  `--parallel --workers 8` and `--parallel auto` becomes plain `--parallel`. An
  option whose value is optional is still greedy in `System.CommandLine`: it
  bound whatever token followed it, so `coyote test --parallel App.dll` took the
  assembly path as a worker count and then failed for a missing assembly, even
  though the help text advertised the value-less form. Taking no value at all
  removes the ambiguity rather than guessing at it, and `--parallel` may now be
  written anywhere on the command line, including before the assembly path.
- **Breaking (command line):** the options whose configuration field is unsigned
  — `--iterations`, `--timeout-delay`, `--deadlock-timeout`, `--max-fuzz-delay`,
  `--resolve-uncontrolled-concurrency-attempts` and
  `--resolve-uncontrolled-concurrency-delay` — now parse as unsigned rather than
  as signed values cast afterwards. Values above `int.MaxValue` were previously
  rejected by the framework's own type conversion, before any validator ran, so
  the upper half of the domain could not be expressed however it was written.
  The wording of the error for a bad value is unchanged.
- Bounded channels of zero capacity — the rendezvous channel added in .NET 10 —
  are now controlled during testing. Each item passes from a writer to a reader
  directly, and whichever side arrives first is paused by the scheduler; these
  channels previously kept the real implementation and so were not observed.
  Prioritized channels still keep the real implementation, since their ordering
  is not modelled, but now report themselves as an uncontrolled invocation
  instead of losing the coverage silently.
- **Breaking (build layout):** build output is now written under a
  configuration-specific directory, so `bin/net8.0` becomes
  `bin/Release/net8.0` and `Tests/X/bin/net8.0` becomes
  `Tests/X/bin/Release/net8.0`. Previously every project overrode `OutputPath`
  to drop the configuration, so debug and release builds wrote to the same
  place and silently overwrote each other; whichever was built last determined
  what `dotnet test --no-build`, the IL-diff validation, and the benchmark
  scripts actually saw. The IL-diff golden hashes have been rebaselined to
  release, which is what `build.ps1` and CI produce, and were previously
  recorded from a debug build so that check could not pass on CI.
  `run-tests.ps1` gained a `-configuration` parameter and now passes it to
  `dotnet test`, which had been defaulting to debug.
- Rewriting configuration files now expand a `$(Configuration)` token in
  `AssembliesPath` and `OutputPath`, alongside the existing
  `$(TargetFramework)`, so that a configuration-specific output directory can
  be named. It is resolved the same way the target framework is.
- Added a `--parallel` option to the `test` command, which shards testing
  iterations across worker processes, each exploring a disjoint range of random
  seeds, and merges their reports and coverage into a single result. Two
  approximations are involved: the per-worker iteration count is rounded up so
  that each worker covers whole rotations of the exploration strategy
  portfolio, and the q-learning and prioritization strategies, which accumulate
  state across iterations, become N independent learners rather than one.
- Reduced per-scheduling-step overhead in the systematic testing runtime. The
  call-site registration injected into every rewritten method no longer takes
  the global runtime lock, no longer grows an unbounded list unless trace
  analysis is enabled, and hashes lazily. Explored paths are now recorded by
  digest rather than by rendering the whole execution trace to a string, and
  visited program states are only recorded when something contributes to the
  state hash. Measured 14-24% faster before these compose with the change
  below, and up to 48% less allocation.
- **Breaking:** execution trace analysis is now disabled by default, because
  building the execution graph allocates on every scheduling step and is only
  consumed when emitting a DGML diagram. Pass `--trace-analysis` (or
  `Configuration.WithTraceAnalysisEnabled()`) to restore the previous behavior.
  A consequence is that the "Visited N unique states" report line no longer
  appears when nothing contributes to the program state hash.
- Added `Tools/SchedulerBench`, a benchmark harness that drives `TestingEngine`
  directly and reports wall-clock time and bytes allocated. The existing
  benchmarks all exercise the production actor runtime, so none of them observe
  changes to the scheduler or the exploration strategies.
- Fixed `TestReport` and `CoverageInfo` losing their synchronization object when
  deserialized, which made a cloned or loaded report throw on merge.
- Rebranded product name, NuGet package IDs (`InterleaveX`, `InterleaveX.Core`,
  `InterleaveX.Actors`, `InterleaveX.Test`, `InterleaveX.Tool`,
  `InterleaveX.CLI`), CLI command (`interleavex`), and documentation to
  InterleaveX.
- Preserved upstream `Microsoft.Coyote.*` C# namespaces, assembly DLL names
  (`Microsoft.Coyote.dll`, `Microsoft.Coyote.Actors.dll`,
  `Microsoft.Coyote.Test.dll`), and internal type names for source/binary
  compatibility with upstream consumers.
- Established dual-licensing: upstream code remains MIT (Microsoft); fork
  additions are GPL-3.0 (see `NOTICE.md` at the working dir root).
- A `coyote` CLI alias for the new `interleavex` command is planned via a
  separate compatibility tool package; until that ships, users may shell-alias
  `coyote` to `interleavex`.

---

## Upstream Microsoft Coyote release notes (retained for reference)

## InterleaveX
- Added support for the `net9.0` target framework.
- Added support for the `net10.0` target framework.
- Added rewriting support for the `System.Threading.Lock` type introduced in
  .NET 9, including `EnterScope`, `Enter`, `Exit`, `TryEnter`, and
  `IsHeldByCurrentThread`.
- Added rewriting support for the `Task.WhenEach` API introduced in .NET 9.
- Added rewriting support for the `Task.WhenAll` and `Task.WhenAny`
  `ReadOnlySpan`-based overloads introduced in .NET 9.
- Added rewriting support for the `new Task(() => ...) + task.Start()` explicit
  task construction pattern, including `Task.RunSynchronously`.
- Added the `IActorRuntime.HaltActorAsync` and `IActorRuntime.HaltAllActorsAsync`
  APIs for externally halting actors and awaiting their full cleanup, including
  `OnHaltAsync` completion.
- The `interleavex test` command now automatically discovers and runs all `[Test]`
  methods when the `-m` flag is omitted, printing per-test banners and an
  aggregate summary. Added the `--list-tests` CLI option for discovering test
  names without running them, and `--stop-on-first-failure` for aborting after
  the first non-success result.
- The `interleavex test` command now emits diagnostic warnings when `[Test]`-decorated
  methods have invalid signatures (e.g. non-public, unsupported parameters)
  instead of silently ignoring them.
- Enhanced the portfolio strategy mode with Q-learning and extended it to the
  fuzzing scheduling policy.
- Optimized the PCT (prioritization) strategy by preserving the awaiting
  operation's group across async task continuations, preventing sequential awaits
  from inflating the exploration space with redundant independent groups.
- Upgraded the `System.Text.Json` package to `v8.0.4` for the `netstandard2.0`
  target framework, due to a vulnerability.
- Dropped support for the `netcoreapp3.1` target framework, which reached end of
  life.

## v1.7.11
- Added support for the `net8.0` target framework.
- Added support to optionally explore a race condition when using the
  `AutoResetEvent.Reset` method.

## v1.7.10
- Fixed an issue with `Actor` not halting as expected in certain scenarios after
  explicitly raising a `HaltEvent` event.

## v1.7.9
- Added the `Microsoft.Coyote.Rewriting.SkipRewriting` attribute that allows
  skipping the rewriting of a user-specified type.
- The `coyote` command line tool can now invoke non-static xUnit tests that have
  no parameters and their declaring type has a constructor without parameters or
  only has the `Xunit.Abstractions.ITestOutputHelper` as parameter.
- Fixed a bug with not reporting correctly actor coverage.

## v1.7.8
- Added rewriting support for fine-grained race-checking at memory-access and
  control-flow branching locations. Race-checking at memory-access locations can
  be enabled during testing by setting the
  `Configuration.WithMemoryAccessRaceCheckingEnabled` option, whereas
  race-checking at control-flow branching locations can be enabled during
  testing by setting the `Configuration.WithControlFlowRaceCheckingEnabled`
  option. Rewriting is enabled by default to support both features, which adds
  extra instructions in the rewritten DLLs, but this can be disabled by setting
  the `IsRewritingMemoryLocations` rewriting option to `false`. 

## v1.7.7
- Added rewriting support for `System.Threading.SpinWait` methods.

## v1.7.6
- Exposed the `ConsoleLogger` as public so that users can conveniently use it to
  write runtime logs to the console.
- Implemented more fake methods in the `ActorTestKit` class.
- Added a method for setting a custom logger when using the `ActorTestKit`
  class.
- Added rewriting support for `System.Threading.Volatile` methods.
- Fixed a bug where merging coverage info could result in a rare race condition.

## v1.7.5
- Added support for controlling user-created `Thread` instances during testing.
- Added support for controlling `WaitHandle` and related APIs during testing.
- Added the `ActorTestKit` class for unit-testing actors and state machines in
  isolation.
- Disabled the automated fallback to randomized fuzzing during testing, if
  systematic testing fails.
- Fixed a bug in bug trace reporting.

## v1.7.4
- Added support for visualizing traces from testing task-based programs in DGML
  format.
- Implemented various runtime optimizations for more efficient coverage during
  testing.
- Optimized the modeling of various lock APIs during testing.
- Fixed a rewriting bug occurring when methods return task arrays.

## v1.7.3
- Added support for the `net7.0` target framework.

## v1.7.2
- Added support for fully controlling the `SemaphoreSlim` type during testing.
- Added support for detecting the `System.Guid` and `System.DateTime` APIs as
  sources of uncontrolled data non-determinism during testing.
- Added the `Configuration.WithPartiallyControlledDataNondeterminismAllowed` API
  (and `--partial-control <MODE>` CLI option) for configuring how uncontrolled
  data non-determinism should be handled during testing.
- Added the `Configuration.WithScheduleCoverageReported` API (and
  `--schedule-coverage` CLI option) for dumping coverage statistics and stack
  traces for scheduling decisions.
- Added the `Specification.RegisterStateHashingFunction` API for registering
  custom program state hashing functions, which can be used to compute an
  approximation of the program state during testing, as well as reporting it in
  the test statistics.
- Improved replay traces by registering the scheduling point type alongside each
  scheduling decision.
- Fixed missing `net462` dependency in the `Microsoft.Coyote.Tool` NuGet
  package.

## v1.7.1
- Added support for operation grouping for `Task` continuations.
- Added support for the delay-bounding exploration strategy.
- Added support for rewriting the `Thread.Yield` and `Interlocked` APIs.
- Updated the runtime to not fail with a potential deadlock when the debugger is
  attached, and instead add a breakpoint, to avoid spurious failures when
  debugging.
- Hardened the `SchedulingPoint.Suppress` and `SchedulingPoint.Resume` methods
  so that they do not resume scheduling earlier than expected when they are used
  in a nested manner.
- Fixed a runtime memory leak when test iterations terminated early.
- Fixed a rare stack-overflow exception when popping states during a
  `StateMachine` execution.
- Fixed a few cases of internally spawned tasks considered to be uncontrolled by
  the runtime.

## v1.7.0
- Updated the default `random` exploration strategy with a `portfolio` testing
  mode that uses a tuned set of different exploration strategies to increase
  coverage for different bug patterns. The portfolio will be transparently
  enhanced over time as new exploration strategies become available inside
  Coyote. The Portfolio can be set to fair or unfair using
  `Configuration.WithPortfolioMode` or the `--portfolio-mode` command-line
  option. The portfolio mode can be disabled and explicitly set to one of the
  available exploration strategies by setting a strategy-related option such as
  `Configuration.WithRandomStrategy` or `-s <STRATEGY>`.
- Refactored the NuGet packages, by moving `Microsoft.Coyote.Actors` to its own
  dedicated package, introducing a new `Microsoft.Coyote.Tool` package that
  contains the self-contained `coyote` command-line tool (for users that do not
  want to manage `coyote` via the `Microsoft.Coyote.CLI` .NET tool), introducing
  a new `Microsoft.Coyote.Core` package that only contains the core runtime
  library of Coyote, and converting the `Microsoft.Coyote` NuGet package into a
  meta-package that pulls all non-tool packages.
- Moved the actor `Event` type under the `Microsoft.Coyote.Actors` namespace.
- Introduced a `Monitor.Event` type (nested in the
  `Microsoft.Coyote.Specifications.Monitor` class), which must now be used for
  declaring specification monitor events, instead of the original `Event` type
  above.
- Enhanced and streamlined the logging API and built-in loggers, which are now
  available in the `Microsoft.Coyote.Logging` namespace, instead of
  `Microsoft.Coyote.IO`.
- Removed support for the end-of-life `net5.0` target framework.

## v1.6.2
- Exposed new `IActorRuntime.GetCurrentActorIds()` API that returns the
  `ActorId` for each active actor managed by the runtime, as well as an
  `IActorRuntime.GetCurrentActorTypes()` API that returns the `Type` of each
  active actor managed by the runtime. These APIs are not thread-safe and should
  only be used for gathering statistics and debugging purposes.

## v1.6.1
- Exposed new `IActorRuntime.GetActorExecutionStatus(id)` API that enables
  querying the actor runtime for the current execution status of the actor with
  the specified id, as well as an `IActorRuntime.GetCurrentActorCount()` API
  that returns the number of active actors managed by the runtime. These APIs
  are not thread-safe and should only be used for gathering statistics and
  debugging purposes.
- Exposed new `IActorRuntime.OnActorHalted` callback which is triggered when an
  actor has halted and the runtime has stopped managing it.

## v1.6.0
- Exposed new `Operation` API that enables instrumenting, controlling and
scheduling custom concurrent operations.
- Exposed new `SchedulingPoint.SetCheckpoint` API that allows to capture all
  non-deterministic decisions in the currently explored execution path and try
  replay them in subsequent test iterations to optimize coverage of a subset of
  the state space.
- Added support for intercepting and controlling asynchronous locks.
- Added support for rewriting the `SemaphoreSlim` type.
- The `Configuration.WithReplayStrategy` method was renamed to
  `Configuration.WithReproducibleTrace` to make it more explicit that setting
  this option allows reproducing the specified trace.
- Various runtime improvements and bug fixes.

## v1.5.9
- Improved the runtime to try enforce atomicity when invoking a specification
  `Monitor`.

## v1.5.8
- Fixed a bug in `coyote rewrite` related to rewriting nested types.

## v1.5.7
- Fixed a bug where a thrown exception was not propagating properly when
  invoking `Task.WaitAll` during systematic testing.
- Fixed a bug in `coyote rewrite` related to return types with nested generics.

## v1.5.6
- Fixed a bug in `coyote rewrite` when checking uncontrolled tasks from methods
  with a nested generic return type.

## v1.5.5
- Added support in `coyote rewrite` for rewriting types with a required modifier
  (`modreq`).

## v1.5.4
- Significantly improved runtime performance during partially-controlled
  concurrency testing.

## v1.5.3
- Improved the assembly loading logic when using the `coyote` tool.
- Fixed rare deadlock in test execution paths that exhibit partially-controlled
  concurrency.
- Various other runtime improvements.

## v1.5.2
- Introduced new command-line interface for the `coyote` tool that builds on top
  of the `System.CommandLine` library. This brings an improved and more robust
  user experience (e.g. better CLI error messages), as well as other
  enhancements such as CLI option grouping.
- The `--coverage code` CLI option is not supported anymore as it was only
  supported on Windows and has been superseded by the official .NET
  cross-platform code coverage infrastructure. See
  [here](https://docs.microsoft.com/en-us/dotnet/core/additional-tools/dotnet-coverage)
  and
  [here](https://docs.microsoft.com/en-us/dotnet/core/testing/unit-testing-code-coverage?tabs=windows).
  The `--coverage` (or `-c`) CLI option is now used to enable activity coverage
  (replacing `--coverage activity`), as discussed
  [here](https://microsoft.github.io/coyote/#how-to/coverage).
- The `--parallel N` CLI option is not supported anymore to bring the `coyote`
  tool experience in line with the programmatic way of running Coyote tests (via
  the `TestingEngine` API), which did not support built-in parallel testing. If
  needed, running parallel tests can still be achieved by invoking multiple
  Coyote testing processes in parallel (e.g. via a script).

## v1.5.1
- Simplified the `coyote` tool ASP.NET dependency.
- Partially controlled concurrency is now allowed by default during systematic
  testing. Disable via the `--no-partial-control` command line option (or
  `Configuration.WithPartiallyControlledConcurrencyAllowed(false)`).
- Added support for schedule space reduction based on read and write operations.
  Enable via the `--reduce-shared-state` command line option (or
  `Configuration.WithSharedStateReductionEnabled`).
- Improved support for detecting potential deadlocks during partially controlled
  concurrency.
- Binary rewriting improvements and fixes.

## v1.5.0
- Added runtime and rewriting support for testing ASP.NET controllers in the
  presence of partially-controlled concurrency.
- Added support for rewriting the `HttpClient` type targeting ASP.NET
  controllers.
- Improved runtime support for partially-controlled concurrency during testing.
- New option for skipping potential deadlocks in the presence of
  partially-controlled concurrency.
- The actor logging method `LogExceptionThrown` is now only called if the
  exception was not handled. The `LogExceptionHandled` method can be used
  instead for handled exceptions.
- Various other runtime improvements and fixes.

## v1.4.3
- Added support for the `netstandard2.0` target framework.
- Added support for rewriting the non-generic `TaskCompletionSource` type.
- Added support for rewriting the `ValueTask` type (but `IValueTaskSource` is
  not supported).
- Improvements to systematic fuzzing, especially for actor-based programs.
- Improvements to how thread interrupts are handled at the end of each test
  iteration.
- Tests now report the degree of concurrency and number of controlled
  operations.

## v1.4.2
- Added support for the `net6.0` target framework.
- The `TestingEngine` is now giving a warning if the DLL being tested has not
  been rewritten.
- The number of controlled operations are now reported as part of test
  statistics.
- Improvements, optimizations and bug-fixes in binary rewriting.
- Added support for dumping the rewritten IL diff to a file through
  `--dump-il-diff`.

## v1.4.1
- Enabled automated fallback to systematic fuzzing upon detecting uncontrolled
  concurrency during testing to increase usability. This feature is enabled by
  default and can be disabled via the `no-fuzzing-fallback` command line option
  (or `Configuration.WithSystematicFuzzingFallbackEnabled`).
- Added a new JSON test report that lists any detected invocations of
  uncontrolled methods.
- The `TestingEngine.TryEmitTraces` method was renamed to
  `TestingEngine.TryEmitReports` to reflect that the reports do not include only
  traces.
- The `IActorRuntimeLog.OnStrategyDescription` method was removed.

## v1.4.0
- Redesigned the systematic testing runtime to significantly improve its
  performance and simplicity.
- An `ActorId` of a halted actor can now be reused.
- The `coyote` tool can now resolve `aspnet`.

## v1.3.1
- Added rewriting support for testing race conditions with several
  `System.Collections.Concurrent` data structures.
- Added rewriting support for testing `System.Collections.Generic.HashSet<T>`
  data races.
- Added the `SchedulingPoint.Suppress` and `SchedulingPoint.Resume` methods for
  suppressing and resuming interleavings of enabled operations, accordingly.
- Fixed a memory leak in the testing engine.

## v1.3.0
- Improved the binary rewriting engine and fixed various rewriting bugs.
- Removed the deprecated `Microsoft.Coyote.Tasks` namespace. Testing task-based
  code should now only be done via binary rewriting, instead of using a custom
  task type.
- Removed the `net48` target framework, can instead just use the `net462` target
  framework for legacy .NET Framework projects.

## v1.2.8
- Improved the strategies used for systematic fuzzing.
- Fixed a rewriting bug related to the `TaskAwaiter` type.

## v1.2.7
- Added the `--no-repro` command line option (enabled also via
  `Configuration.WithNoBugTraceRepro`), which disables the ability to reproduce
  buggy traces to allow skipping errors due to uncontrolled concurrency, for
  example when the program is only partially rewritten, or there is external
  concurrency that is not mocked, or when the program uses an API that is not
  yet supported.
- The uncontrolled concurrency errors have been updated to be more informative
  and point to the documentation for further reading.

## v1.2.6
- Added an experimental rewriting pass that adds assertion checks to find data
  races in uses of the `System.Collections.Generic.List<T>` and
  `System.Collections.Generic.Dictionary<TKey, TValue>` collections.
- Added support for the `net462` target framework.

## v1.2.5
- Added the `SchedulingPoint` static class that exposes methods for adding
  manual scheduling points during systematic testing.
- Added an experimental systematic testing strategy that uses reinforcement
  learning. This is enabled using the `--sch-rl` command line option or the
  `Configuration.WithRLStrategy` method.
- Added an experimental systematic fuzzing testing mode that uses delay
  injection instead of systematic testing to find bugs. This can be enabled
  using the `--systematic-fuzzing` command line option or the
  `Configuration.WithSystematicFuzzingEnabled` method.
- Added the `IActorRuntimeLog.OnEventHandlerTerminated` actor log callback that
  is called when an event handler terminates.
- Fixed a bug where the `IActorRuntimeLog.OnHandleRaisedEvent` actor log
  callback was not invoked in production.

## v1.2.4
- Improved how `coyote test` resolves ambiguous test method names.
- Fixed a bug where awaiting a task from a previous test iteration that was
  canceled due to `ExecutionCanceledException` would hang the tester.

## v1.2.3
- Exposed the `TextWriterLogger` type.
- Fixed a configuration bug where the `fairpct` strategy would be picked instead
  of `probabilistic`.

## v1.2.2
- Added the `Specification.IsEventuallyCompletedSuccessfully` API for checking
  if a task eventually completes successfully.
- Added the `Configuration.WithTestingTimeout` API for specifying a systematic
  testing timeout instead of iterations.
- Optimized state space exploration in programs using `Task.Delay`.
- Added support for the `net5.0` target framework.
- Removed the `net47` target framework.

## v1.2.1
- Added the `OnEventIgnored` and `OnEventDeferred` callbacks in the `Actor`
  type.

## v1.2.0
- Added support for systematically testing actors and tasks together using
  rewriting.
- Hardened the systematic testing runtime.

## v1.1.5
- Improved detection of uncontrolled tasks during systematic testing.
- Added detection of invoking unsupported APIs during systematic testing.

## v1.1.4
- Added missing `coyote rewrite` dependencies in the `Microsoft.Coyote.Test`
  package.

## v1.1.3
- Optimizations and fixes in binary rewriting.

## v1.1.2
- Added basic support for the `System.Threading.Tasks.Parallel` type during
  rewriting.
- Fixed a bug in `coyote rewrite` that was incorrectly copying dependencies
  after rewriting.

## v1.1.1
- Renamed `TestingEngine.ReproducibleTrace` to fix typo in the API name.
- Fixed some bugs in `coyote rewrite`.

## v1.1.0
- Added experimental support for testing unmodified task-based programs using
  binary rewriting.
- Added support for log severity in the logger and converted to an `ILogger`
  interface.
- Optimized various internals of the task testing runtime.

## v1.0.17
- Fixed a bug in the `Actor` logic related to event handlers.
- Fixed a bug in `Microsoft.Coyote.Task.WhenAny`.

## v1.0.16
- Added support for cancellations in `Task.Run` APIs.
- Optimized various internals of the task testing runtime.

## v1.0.15
- Fixed the `Task.WhenAny` and `Task.WhenAll` APIs so that they execute
  asynchronously during systematic testing.
- Fixed the `Task.WhenAny` and `Task.WhenAll` APIs so that they throw the proper
  argument exceptions during systematic testing.

## v1.0.14
- Added missing `Task<TResult>.UncontrolledTask` API.
- Fixed a bug in the testing runtime for controlled tasks.

## v1.0.13
- Fixed a bug in the testing runtime for controlled tasks that could lead to a
  stack overflow.
- Optimized various internals of the testing runtime.

## v1.0.12
- Introduced a new `EventGroup` API for actors, which replaces operation groups,
  that allows improved tracing and awaiting of long running actor operations.
- The `Task.Yield` API can now be used to de-prioritize the executing operation
  during testing.
- Added missing APIs in the `Microsoft.Coyote.Tasks.Semaphore` type.
- Fixed two bugs in the systematic testing scheduler.

## v1.0.11
- Fixed an issue that did not allow systematic and non-systematic unit tests to
  run on the same process.
- Fixed a bug in the `TestingEngine` logger.

## v1.0.10
- Fixed the NuGet symbol packages.

## v1.0.9
- Introduced a new `Microsoft.Coyote.Test` package that contains the `Test`
  attribute and the `TestingEngine` type for writing unit tests.
- The core `Microsoft.Coyote` does not contain anymore `Test` and
  `TestingEngine`, which were moved to the `Microsoft.Coyote.Test` package.
- Added support for optional anonymized telemetry in the `TestingEngine`.
- Optimized various internals of the systematic testing scheduler.
- Fixed some issues in the scripts.

## v1.0.8
- The core `Microsoft.Coyote` project is now targeting only .NET Standard,
  allowing it to be consumed by any project that supports `netstandard2.0` and
  above.
- Removed the `net46` target.
- Fixed bug in using the global dotnet tool.

## v1.0.7
- Added support for building Coyote on Linux and macOS.
- Building Coyote locally now ignores .NET targets that are not installed.
- Added optional anonymized telemetry in the `coyote` tool.
- Fixed a bug in the `SynchronizedBlock` type.

## v1.0.6
- Added a `SynchronizedBlock` type to model the semantics of the C# `lock`
  statement.
- Simplified the `Configuration` APIs for setting max-steps and liveness related
  heuristics.
- Fixed code coverage and added support for code coverage on `netcoreapp3.1`.

## v1.0.5
- Added a --version argument to the `coyote` command line tool.
- Added a dotnet tool package called `Microsoft.Coyote.CLI` to install the
  `coyote` command line tool and running it without an explicit path.
- Exposed the `ReadableTrace` and `ReproducibleTrace` members of
  `Microsoft.Coyote.SystematicTesting.TestingEngine` as public.
- Fixed a bug in activity coverage reporting for `netcoreapp3.1`.
- Fixed some bugs in parallel testing.

## v1.0.4
- Added new `Microsoft.Coyote.Configuration.WithReplayStrategy` method for
  programmatically assigning a trace to replay.
- Added support for the `netstandard2.1`, `netcoreapp3.1` and `net48` targets.
- Removed support for the `netcoreapp2.2` target, which reached end of life.
- Fixed various bugs in the documentation.

## v1.0.3
- Fixed an issue when invoking
  `Microsoft.Coyote.Tasks.Task.ExploreContextSwitch` during a production run.

## v1.0.2
- Made ActorRuntimeLogGraphBuilder public.
- Added CreateStateMachine to IActorRuntimeLog.

## v1.0.1
- Fixed an issue in the runtime (there should always be a default task runtime
  instance).

## v1.0.0
- The initial release of the Coyote set of libraries and test tools.