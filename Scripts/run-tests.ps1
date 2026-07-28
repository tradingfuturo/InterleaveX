# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Modifications are licensed under the GNU General Public License v3.0 or
# later. See LICENSE-GPL for the full text.

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

# Checked here as well as in CI: a reference to the product output that forgets the configuration
# resolves to whichever build happened to run last, and the resulting failure surfaces somewhere
# else entirely.
Write-Comment -text "Checking the build output layout." -color "blue"
& $PSScriptRoot/check-build-layout.ps1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $PSScriptRoot/check-script-helpers.ps1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

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

# A target that contributes no test run has to fail the script. Neither way of contributing nothing
# reports itself: an unbuilt configuration is only a non-terminating Get-ChildItem error, and a
# '-framework' that names one nobody built is filtered out in silence. Both used to reach the final
# 'Done' with exit code 0, so an explicit selection could run nothing and still pass.
$unbuilt_targets = @()
$empty_targets = @()

foreach ($kvp in $targets.GetEnumerator()) {
    if (($test -ne "all") -and ($test -ne $($kvp.Name))) {
        continue
    }

    $output_dir = "$PSScriptRoot/../Tests/$($kvp.Value)/bin/$configuration"
    if (-not (Test-Path $output_dir)) {
        $unbuilt_targets += $($kvp.Value)
        continue
    }

    $ran_for_target = 0
    $frameworks = Get-ChildItem -Path $output_dir | `
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
        $ran_for_target = $ran_for_target + 1
    }

    if ($ran_for_target -eq 0) {
        $empty_targets += $($kvp.Value)
    }
}

if ($unbuilt_targets.Count -gt 0) {
    Write-Error "no '$configuration' build found for: $($unbuilt_targets -join ', ')."
    Write-Error "Build that configuration first, for example: ./Scripts/build.ps1 -configuration $configuration"
    exit 1
}

if ($empty_targets.Count -gt 0) {
    Write-Error "no tests ran for: $($empty_targets -join ', ')."
    if ($ci.IsPresent) {
        Write-Error "The '$configuration' build produced none of the target frameworks."
    } else {
        Write-Error "The '$configuration' build has no '$framework' output. Build it, or pass a -framework that was built."
    }

    exit 1
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
    $bench = "$PSScriptRoot/../Tools/SchedulerBench/bin/$configuration/net10.0/SchedulerBench.dll"
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

        # Exploration must not depend on anything that varies from process to process. String hash
        # codes are randomized per process and feed the program state that q-learning keys on, so
        # this is the one check no single-process test can make. The scheduling coverage file is
        # the signal because it summarizes every decision of all 20 iterations rather than just
        # the pass/fail of the run; it differs whenever the seed does, so it is not vacuous.
        $determinism_dir = Join-Path ([System.IO.Path]::GetTempPath()) ("interleavex-determinism-" + [System.Guid]::NewGuid().ToString("N"))
        $runs = @(1, 2) | ForEach-Object {
            $run_dir = Join-Path $determinism_dir "run$_"
            $out = (& "$cli_tool_path/interleavex" test $bench -m NoBug -i 20 --seed 1 `
                -s q-learning --schedule-coverage -o $run_dir) -join '\n'
            if ($LASTEXITCODE -ne 0) {
                Remove-Item -LiteralPath $determinism_dir -Recurse -Force -ErrorAction SilentlyContinue
                Assert-Failed "The cross-process determinism run failed" $out
            }

            $coverage = Get-ChildItem -Path $run_dir -Recurse -Filter "*.coverage.schedule.txt" |
                Select-Object -First 1
            if ($null -eq $coverage) {
                Remove-Item -LiteralPath $determinism_dir -Recurse -Force -ErrorAction SilentlyContinue
                Assert-Failed "The cross-process determinism run emitted no scheduling coverage" $out
            }

            (Get-FileHash -LiteralPath $coverage.FullName -Algorithm SHA256).Hash
        }

        Remove-Item -LiteralPath $determinism_dir -Recurse -Force -ErrorAction SilentlyContinue
        if ($runs[0] -ne $runs[1]) {
            Assert-Failed "Exploration differed across processes" "$($runs[0])`n$($runs[1])"
        }
    }

    Remove-Item $cli_tool_path -Recurse
}

Write-Comment -text "Done." -color "green"
