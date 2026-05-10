# `InterleaveX`

[![NuGet](https://img.shields.io/nuget/v/InterleaveX.svg)](https://www.nuget.org/packages/InterleaveX/)
[![Nuget](https://img.shields.io/nuget/dt/InterleaveX?color=informational)](https://www.nuget.org/packages/InterleaveX/)

**InterleaveX is a community-maintained fork of [Microsoft Coyote](https://github.com/microsoft/coyote)** — a cross-platform library and tool for testing concurrent C# code and deterministically reproducing bugs.

Using InterleaveX, you can test the *concurrency* and other *nondeterminism* in your C# code by writing what is called a *concurrency unit test*. These look like regular unit tests, but reliably exercise concurrent workloads (actors, tasks, concurrent requests to ASP.NET controllers, and so on). In regular unit tests you would typically avoid concurrency because of flakiness; with InterleaveX you are encouraged to embrace concurrency in your tests to find bugs.

## Why this fork exists

We — a small group of engineers maintaining production .NET software that depends on systematic concurrency testing — created InterleaveX to keep the toolchain actively maintained on current and upcoming .NET LTS releases.

The upstream [Microsoft Coyote](https://github.com/microsoft/coyote) project remains the authoritative reference for the design, the research, and the original implementation. InterleaveX exists to:

- Track .NET runtime updates as they ship and ensure the rewriter and runtime keep working.
- Continue evolving instrumentation against newer BCL APIs that production codebases adopt.
- Fix bugs we encounter in our own use of the tool.

We do not aim to fork the design or the research credit. All credit for the systematic concurrency testing approach goes to the Microsoft Coyote team.

## Relationship to Microsoft Coyote

- **Internal namespaces (`Microsoft.Coyote.*`), assembly DLL names, and class names are intentionally kept unchanged.** This preserves source and binary compatibility with upstream — porting an existing Microsoft.Coyote consumer to InterleaveX should typically only require swapping NuGet package references from `Microsoft.Coyote.*` to `InterleaveX.*`.
- **NuGet packages are republished under the `InterleaveX.*` prefix:** `InterleaveX`, `InterleaveX.Core`, `InterleaveX.Actors`, `InterleaveX.Test`, `InterleaveX.Tool`, `InterleaveX.CLI`.
- **The CLI command is `interleavex`.** A `coyote` alias for backwards compatibility with existing scripts is planned via a separate compatibility tool package; for now, users of the fork can shell-alias `coyote` to `interleavex`.
- **Strong-name signing key is fork-owned**, not Microsoft's.
- **Licensing is dual:** Microsoft's original code remains under MIT (see [`LICENSE`](LICENSE)). New fork-authored modifications are licensed under GPL-3.0 (see [`NOTICE.md`](NOTICE.md) for the full dual-licensing explanation).

## How it works

Consider this simple test:

```csharp
[Fact]
public async Task TestTask()
{
  int value = 0;
  Task task = Task.Run(() =>
  {
    value = 1;
  });

  Assert.Equal(0, value);
  await task;
}
```

This test passes most of the time because the assertion typically executes before the task starts, but there is one schedule where the task starts fast enough to set `value` to `1` causing the assertion to fail. This is a deliberately naive example; real codebases hide much more complicated race conditions in complex execution paths.

You convert the test to a concurrency unit test using the `TestingEngine` API:

```csharp
using Microsoft.Coyote.SystematicTesting;

[Fact]
public async Task InterleaveXTestTask()
{
  var configuration = Configuration.Create().WithTestingIterations(10);
  var engine = TestingEngine.Create(configuration, TestTask);
  engine.Run();
}
```

(Note: the `using Microsoft.Coyote.SystematicTesting;` namespace is intentionally retained — see "Relationship to Microsoft Coyote" above.)

Next, run the `interleavex rewrite` command from the CLI (typically as a post-build task) to automatically rewrite the IL of your test and production binaries. This allows the engine to inject hooks that take control of the concurrent execution during testing.

You can then run the concurrent unit test from your favorite unit testing framework (e.g. [xUnit](https://xunit.net/)). InterleaveX will take over and repeatedly execute the test from beginning to end for N iterations (10 in the above example). Under the hood, intelligent search strategies explore execution paths that might hide a bug in each iteration.

Once a bug is found, the trace returned by `engine.TestReport` lets you reliably *reproduce* the bug as many times as you want, making debugging significantly easier.

## Get started

Install the `interleavex` CLI as a global .NET tool:

```pwsh
dotnet tool install --global InterleaveX.CLI
```

Or add the InterleaveX libraries to your project:

```pwsh
dotnet add package InterleaveX
```

For tutorials, conceptual documentation, how-tos, and samples, see the upstream Microsoft Coyote [website](https://microsoft.github.io/coyote/) — most material applies verbatim to InterleaveX since the API surface is preserved. Fork-specific notes live in [`docs/overview/fork-rationale.md`](docs/overview/fork-rationale.md).

Upgrading your dependencies? Check the changelog in [History.md](History.md).

## Support

InterleaveX is provided "as-is". The fork maintainers do not provide formal support. Issues and contributions are welcome through the fork's GitHub repository.

For questions about the original Coyote design, research, or upstream releases, refer to the [Microsoft Coyote repository](https://github.com/microsoft/coyote).

## Contributing

Contributions are welcome. Submitted contributions to InterleaveX are licensed under GPL-3.0 (see [`NOTICE.md`](NOTICE.md)). By submitting a pull request you agree that your contribution is licensed under those terms and that you have the right to grant such a license.

When in doubt about whether a change belongs upstream (in Microsoft Coyote) or in InterleaveX, prefer upstream — InterleaveX explicitly aims to stay close to upstream and only diverges where necessary for active maintenance on newer .NET versions.

## Acknowledgements

InterleaveX would not exist without Microsoft Coyote. We are grateful to the Microsoft Coyote team for the original research, design, implementation, and the permissive MIT license that makes this fork possible.
