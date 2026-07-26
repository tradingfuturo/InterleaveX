# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

param(
    [ValidateSet("net10.0", "net9.0", "net8.0", "net6.0", "net462")]
    [string]$framework = "net10.0",
    [ValidateSet("all", "runtime", "rewriting", "testing", "actors", "actors-testing", "tools")]
    [string]$test = "all",
    [string]$filter = "",
    [string]$logger = "",
    [ValidateSet("quiet", "minimal", "normal", "detailed", "diagnostic")]
    [string]$v = "normal",
    [ValidateSet("Debug", "Release")]
    [string]$configuration = "Release",
    [switch]$cli,
    [switch]$ci
)

Import-Module $PSScriptRoot/common.psm1 -Force

$all_frameworks = (Get-Variable "framework").Attributes.ValidValues
$targets = [ordered]@{
    "runtime" = "Tests.Runtime"
    "rewriting" = "Tests.Rewriting"
    "testing" = "Tests.BugFinding"
    "actors" = "Tests.Actors"
    "actors-testing" = "Tests.Actors.BugFinding"
    "tools" = "Tests.Tools"
}

# Find that paths to the installed .NET runtime.
$dotnet = "dotnet"
$dotnet_runtime_path = FindDotNetRuntimePath -dotnet $dotnet -runtime "NETCore"
$aspnet_runtime_path = FindDotNetRuntimePath -dotnet $dotnet -runtime "AspNetCore"
$runtime_version = FindDotNetRuntimeVersion -dotnet_runtime_path $dotnet_runtime_path

# NOTE: we do some hacks to get around a known issue with dotnet tool
# command being available after locally being restored.
# Example: https://github.com/dotnet/sdk/issues/11820
# Restore the local ilverify tool.
&dotnet nuget locals all --clear
&dotnet tool restore
&dotnet tool install dotnet-ilverify --version 10.0.0
&dotnet tool list
$ilverify = "dotnet ilverify"

[System.Environment]::SetEnvironmentVariable('COYOTE_CLI_TELEMETRY_OPTOUT', '1')

# Run all enabled tests.
Write-Comment -text "Running the Coyote tests." -color "blue"
foreach ($kvp in $targets.GetEnumerator()) {
    if (($test -ne "all") -and ($test -ne $($kvp.Name))) {
        continue
    }

    $frameworks = Get-ChildItem -Path "$PSScriptRoot/../Tests/$($kvp.Value)/bin/$configuration" | `
        Where-Object Name -CIn $all_frameworks | Select-Object -expand Name
    foreach ($f in $frameworks) {
        if ((-not $ci.IsPresent) -and ($f -ne $framework)) {
            continue
        }

        $target = "$PSScriptRoot/../Tests/$($kvp.Value)/$($kvp.Value).csproj"
        if ($f -eq "net10.0" -or $f -eq "net9.0" -or $f -eq "net8.0") {
            $AssemblyName = GetAssemblyName($target)
            $command = [IO.Path]::Combine($PSScriptRoot, "..", "Tests", $($kvp.Value), "bin", $configuration, $f, "$AssemblyName.dll")
            $command = $command + ' -r "' + [IO.Path]::Combine( `
                $PSScriptRoot, "..", "Tests", $($kvp.Value), "bin", $configuration, $f, "*.dll") + '"'
            $command = $command + ' -r "' + [IO.Path]::Combine($PSScriptRoot, "..", "bin", $configuration, $f, "*.dll") + '"'
            $command = $command + ' -r "' + [IO.Path]::Combine($dotnet_runtime_path, $runtime_version, "*.dll") + '"'
            $command = $command + ' -r "' + [IO.Path]::Combine($aspnet_runtime_path, $runtime_version, "*.dll") + '"'
            # Exclude the compiler-generated <PrivateImplementationDetails>.InlineArrayAsReadOnlySpan
            # helper. ilverify (10.0.0) raises a false-positive ReturnPtrToStack on it because it does
            # not model [InlineArray] ref semantics. This helper is emitted by Roslyn for collection
            # expressions and is unrelated to the binary rewriter: the identical error is present in the
            # pre-rewrite (obj) assembly, so excluding it does not weaken corruption detection.
            $command = $command + ' -e "InlineArrayAsReadOnlySpan"'
            Invoke-ToolCommand -tool $ilverify -cmd $command -error_msg "found corrupted assembly rewriting"
        }

        Invoke-DotnetTest -dotnet $dotnet -project $($kvp.Name) -target $target `
            -filter $filter -logger $logger -framework $f -verbosity $v -configuration $configuration
    }
}

if ($cli.IsPresent -and $IsWindows) {
    Write-Comment -text "Running the InterleaveX CLI NuGet tool installation test." -color "blue"

    $ErrorActionPreference = 'Stop'
    $temp_path = "bin/temp"
    $cli_tool_path = "$PSScriptRoot/../$temp_path"
    New-Item -Path $cli_tool_path -ItemType Directory -Force | out-null
    if (Test-Path $cli_tool_path/interleavex.exe) {
        Write-Comment -text "Uninstalling the InterleaveX.CLI package."
        dotnet tool uninstall InterleaveX.CLI --tool-path $temp_path
    }

    Write-Comment -text "Installing the InterleaveX.CLI package."
    dotnet tool install --add-source $PSScriptRoot/../bin/$configuration/nuget InterleaveX.CLI --no-cache --tool-path $temp_path

    $help = (& "$cli_tool_path/interleavex" -?) -join '\n'
    if (!$help.Contains("interleavex [command] [options]")) {
        Remove-Item $cli_tool_path -Recurse
        Write-Error "### Unexpected output from interleavex command"
        Write-Error $help
        Exit 1
    }

    Write-Comment -text "Running the command-line acceptance tests."
    $bench = "$PSScriptRoot/../Tools/SchedulerBench/bin/Release/net10.0/SchedulerBench.dll"
    if (Test-Path $bench) {
        # Fails the run, after cleaning up the temporary tool install.
        function Assert-Failed($reason, $output) {
            Remove-Item $cli_tool_path -Recurse
            Write-Error "### $reason"
            Write-Error $output
            Exit 1
        }

        # '--parallel' carries no value, so it must shard from any position on the command line.
        # It used to bind whatever token followed it, which made the third ordering below consume
        # the assembly path and fail.
        $orderings = @(
            @("test", $bench, "-m", "NoBug", "-i", "20", "--seed", "1", "--parallel"),
            @("test", $bench, "--parallel", "-m", "NoBug", "-i", "20", "--seed", "1"),
            @("test", "--parallel", $bench, "-m", "NoBug", "-i", "20", "--seed", "1"),
            @("test", "--parallel", "--workers", "4", $bench, "-m", "NoBug", "-i", "20", "--seed", "1")
        )

        foreach ($ordering in $orderings) {
            $written = $ordering -join ' '
            $parallel = (& "$cli_tool_path/interleavex" @ordering) -join '\n'
            if ($LASTEXITCODE -ne 0) {
                Assert-Failed "Sharded testing failed for 'interleavex $written'" $parallel
            }

            if (!$parallel.Contains("Explored 20 execution paths")) {
                Assert-Failed "Sharded testing did not explore every iteration for 'interleavex $written'" $parallel
            }
        }

        # The worker count is meaningless without the flag that turns sharding on. Both streams are
        # captured here, unlike above, because a parse error is reported on standard error while the
        # usage text that follows it goes to standard output.
        $orphan = (& "$cli_tool_path/interleavex" test $bench -m NoBug -i 20 --workers 4 2>&1 | Out-String)
        if ($LASTEXITCODE -eq 0 -or !$orphan.Contains("requires option 'parallel'")) {
            Assert-Failed "'--workers' without '--parallel' should have been rejected" $orphan
        }

        # The iteration count is unsigned in the configuration, so the whole unsigned range has to
        # reach it. Bounded by a timeout, because the point is that the count is accepted.
        $unsigned = (& "$cli_tool_path/interleavex" test $bench -m NoBug -i 4294967295 -t 1) -join '\n'
        if ($LASTEXITCODE -ne 0) {
            Assert-Failed "An iteration count above int.MaxValue should have been accepted" $unsigned
        }
    }

    Remove-Item $cli_tool_path -Recurse
}

Write-Comment -text "Done." -color "green"
