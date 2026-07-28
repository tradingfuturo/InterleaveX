## Build from source

If you plan to contribute a Pull Request to InterleaveX then you need to be able to build the
source code and run the tests.

<a href="https://github.com/tradingfuturo/interleavex" class="btn btn-primary mt-20" target="_blank">Clone
the github repo</a>

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet), which is what `global.json` pins.
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell),
  which the build scripts require. On Windows, `powershell` is the built-in 5.1 and the scripts
  refuse to run under it, so invoke them as `pwsh` throughout.

The .NET 9.0 and 8.0 runtimes are also worth installing: the projects target all three, and
`run-tests.ps1 -ci` runs the tests against each.

**Optional:**

- [Visual Studio 2022](https://docs.microsoft.com/en-us/visualstudio/install/install-visual-studio)
on Windows.
- [Visual Studio Code](https://code.visualstudio.com/Download) is handy to have on other platforms.

### Building the InterleaveX project

Clone the [InterleaveX repo](https://github.com/tradingfuturo/interleavex), then open
`InterleaveX.sln` and build.

You can also use the following `PowerShell` command line from a Visual Studio 2022 Developer
Command Prompt:

```plain
pwsh -File Scripts/build.ps1
```

### Building the NuGet packages

In the InterleaveX project run this `PowerShell` command line from a Visual Studio 2022 Developer
Command Prompt:

```plain
pwsh -File Scripts/build.ps1 -nuget -ci
```

The packages are written to `bin/Release/nuget`. Both switches are required: `build.ps1` skips
packing unless `-nuget` and `-ci` are given together, and packing is supported only on Windows.

### Installing the InterleaveX command line tool package

You can install the `interleavex` tool from this locally built package using:

```plain
dotnet tool install --global --add-source ./bin/Release/nuget InterleaveX.CLI
```

To update your version of the tool you will have to first uninstall the previous version using:

```plain
dotnet tool uninstall --global InterleaveX.CLI
```

Now you are ready to [start using InterleaveX](using-coyote.md).

### Running the tests

To run all available tests, execute the following `PowerShell` command line from a Visual Studio
2022 Developer Command Prompt:

```plain
pwsh -File Scripts/run-tests.ps1
```

You can also run a specific category of tests by adding the `-test` option to specify the category
name. The available categories are `all`, `runtime`, `rewriting`, `testing`, `actors`,
`actors-testing` and `tools`, for example:

```plain
pwsh -File Scripts/run-tests.ps1 -test runtime
```
