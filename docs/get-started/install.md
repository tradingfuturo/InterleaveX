## Installing InterleaveX

InterleaveX (a fork of [Microsoft Coyote](https://github.com/microsoft/coyote)) is an open-source cross-platform .NET library and tool, which means it can be used on Windows, Linux, and macOS.

### Prerequisites

Install the [.NET SDK](https://dotnet.microsoft.com/download/dotnet) for one of the .NET target frameworks supported by InterleaveX:

| Target Framework      | Operating System      |
| :-------------------: | :-------------------: |
| .NET 10.0             | Linux, macOS, Windows |
| .NET 9.0              | Linux, macOS, Windows |
| .NET 8.0              | Linux, macOS, Windows |
| .NET Standard 2.0     | Linux, macOS, Windows |
| .NET Framework 4.6.2  | Windows               |

Learn more about the .NET target frameworks
[here](https://learn.microsoft.com/en-us/dotnet/standard/frameworks) and .NET Standard
[here](https://learn.microsoft.com/en-us/dotnet/standard/net-standard). InterleaveX aims to support new .NET target frameworks as they are released, and until they reach
[end-of-life](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).

Additionally, you can **optionally** install:
- [Visual Studio Code](https://code.visualstudio.com/Download), which is cross-platform.
- [Visual Studio 2022](https://docs.microsoft.com/en-us/visualstudio/install/install-visual-studio)
if you are on Windows.

### Installing the InterleaveX NuGet packages

The InterleaveX libraries can be installed by adding the
[`InterleaveX.Core`](https://www.nuget.org/packages/InterleaveX.Core/) NuGet package and
the [`InterleaveX.Test`](https://www.nuget.org/packages/InterleaveX.Test/) NuGet package
to your C# project. The API surface is identical to upstream Microsoft Coyote — your `using` statements stay `using Microsoft.Coyote.SystematicTesting;`, your type references stay `Microsoft.Coyote.Actors.Actor`, and so on. Only the package references differ.

You can manually add InterleaveX to your C# project by using:
```plain
dotnet add <yourproject>.csproj package InterleaveX.Core
dotnet add <yourproject>.csproj package InterleaveX.Test
```

Alternatively, InterleaveX provides the
[`InterleaveX`](https://www.nuget.org/packages/InterleaveX/) NuGet meta-package, which
includes all the other InterleaveX packages. You can install it using:
```plain
dotnet add <yourproject>.csproj package InterleaveX
```

If you are migrating from upstream `Microsoft.Coyote.*` packages, the change is purely the NuGet IDs. Your source code does not need to change. See the [fork rationale](../overview/fork-rationale.md) for details.

### Installing the InterleaveX tool

You can install and use the cross-platform `interleavex` command-line tool without having to build InterleaveX from source. To use `interleavex` from the command line, you must first install it as a `dotnet tool` using the following command:
```plain
dotnet tool install --global InterleaveX.CLI
```
Using the `--global` flag installs `interleavex` for the current user. You can update the global `interleavex` tool by running:
```plain
dotnet tool update --global InterleaveX.CLI
```
You can remove the global `interleavex` tool by running:
```plain
dotnet tool uninstall --global InterleaveX.CLI
```

Alternatively, to install the tool **locally** on a specific repo, so anyone who clones the repo gets access to the same version that you use, run the following command from the root of that repo:
```
dotnet new tool-manifest
```

This creates a new `<path>/.config/dotnet-tools.json` file. (You can skip this step if you already have such a .NET tool manifest file in your repo.) Now you can install the `interleavex` tool using:
```bash
dotnet tool install --local InterleaveX.CLI
# You can invoke the tool from this directory using the following commands:
#  'dotnet tool run interleavex' or 'dotnet interleavex'.
# Tool 'interleavex.cli' (version '...') was successfully installed.
# Entry is added to the manifest file <path>/.config/dotnet-tools.json.
```

The `interleavex` tool can now be version-controlled in your repo, so that it can easily be shared with other developers. The `<path>/.config/dotnet-tools.json` file will look like this:
```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "interleavex.cli": {
      "version": "...",
      "commands": [
        "interleavex"
      ]
    }
  }
}
```

Each time you clone your repo and want to restore the `interleavex` tool, run:
```bash
dotnet tool restore
```

You can also edit the `<path>/.config/dotnet-tools.json` file to upgrade the version of `interleavex` and run the same `dotnet tool restore` command to upgrade the tool.

Learn more about .NET tools and how to manage them
[here](https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools).

**Note:** the above command-line tool is only for the cross-platform .NET target frameworks. If you prefer not to manage `interleavex` as a .NET tool (via the `dotnet tool` command), or you need a version of the executable that runs on .NET Framework for Windows, then you can instead download the [`InterleaveX.Tool`](https://www.nuget.org/packages/InterleaveX.Tool/) NuGet package, which includes the self-contained tool for all supported target frameworks. To install this package run:
```plain
dotnet add package InterleaveX.Tool
```
You will find the `interleavex` executable in the `tools\<dotnet_target_framework>` directory of the package, and you can run it from inside that directory.

### Using the InterleaveX tool

You can now start using the `interleavex` command-line tool. Type `interleavex --help` to see if it is working. To learn how to use it, read [here](using-coyote.md).

If your existing scripts/CI invoke the upstream `coyote` command, a `coyote` alias for `interleavex` is on the roadmap as a separate compatibility tool package. Until that ships, you can shell-alias `coyote` to `interleavex` (e.g., `Set-Alias coyote interleavex` in your PowerShell `$PROFILE`, or `alias coyote=interleavex` in bash).

### Troubleshooting

#### The element 'metadata' in namespace nuspec.xsd has invalid child element 'repository'...

If you get an error building the NuGet package, you may need to download a new version of `nuget.exe` from [https://www.nuget.org/downloads](https://www.nuget.org/downloads).
