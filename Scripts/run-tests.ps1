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
    [switch]$ci,
    # Runs the targets one after another instead of together. The two modes reach their verdict
    # through the same aggregation, so this changes only how long the run takes.
    [switch]$sequential,
    # Skips clearing the NuGet caches and reinstalling the local tools. That setup costs more than
    # some of the targets it precedes, and it runs again on every invocation, so timing anything
    # against a run that included it measures the setup rather than the tests.
    [switch]$noSetup,
    # Skips the layout and helper gates below. They are preconditions on the tree rather than part
    # of a test run, and check-script-helpers.ps1 drives this script to prove that a run which tests
    # nothing fails -- so without this the two would call each other until the call depth ran out.
    [switch]$noGates,
    # How many targets to run at once. Zero picks one per core, bounded by the number of targets.
    [int]$throttle = 0
)

Import-Module $PSScriptRoot/common.psm1 -Force

# Checked here as well as in CI: a reference to the product output that forgets the configuration
# resolves to whichever build happened to run last, and the resulting failure surfaces somewhere
# else entirely.
if (-not $noGates.IsPresent) {
    Write-Comment -text "Checking the build output layout." -color "blue"
    & $PSScriptRoot/check-build-layout.ps1
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    & $PSScriptRoot/check-script-helpers.ps1
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

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
if (-not $noSetup.IsPresent) {
    &dotnet nuget locals all --clear
    &dotnet tool restore
    &dotnet tool install dotnet-ilverify --version 10.0.0
    &dotnet tool list
}

$ilverify = "dotnet ilverify"

[System.Environment]::SetEnvironmentVariable('COYOTE_CLI_TELEMETRY_OPTOUT', '1')

# Run all enabled tests.
Write-Comment -text "Running the Coyote tests." -color "blue"

# A target that contributes no test run has to fail the script. Neither way of contributing nothing
# reports itself: an unbuilt configuration is only a non-terminating Get-ChildItem error, and a
# '-framework' that names one nobody built is filtered out in silence. Both used to reach the final
# 'Done' with exit code 0, so an explicit selection could run nothing and still pass.
#
# Each target is now run by 'Invoke-TestTargetShard', which returns what happened rather than ending
# the run, and the verdict is reached by 'Test-ShardOutcome'. Both exist because the targets are run
# together by default: a shard reporting a failure the way the rest of these scripts do, by calling
# 'exit', would end its own runspace while the parent finished and reported success.
$context = @{
    ScriptRoot = $PSScriptRoot
    Configuration = $configuration
    Framework = $framework
    AllFrameworks = $all_frameworks
    IsCi = $ci.IsPresent
    Filter = $filter
    Logger = $logger
    Verbosity = $v
    Dotnet = $dotnet
    Ilverify = $ilverify
    DotnetRuntimePath = $dotnet_runtime_path
    AspnetRuntimePath = $aspnet_runtime_path
    RuntimeVersion = $runtime_version
}

$selected = @($targets.GetEnumerator() | Where-Object { ($test -eq "all") -or ($test -eq $_.Name) })
$expected_targets = @($selected | ForEach-Object { $_.Value })

if ($throttle -le 0) {
    # One shard per core, and never more shards than there are targets to run. Each shard is a
    # 'dotnet test' process exploring schedules, which saturates a core for as long as it runs, and
    # every test in the suite carries a five second timeout. Running more of them than the machine
    # has cores does not just take longer -- it makes tests that were only ever going to be slow
    # start failing, which on a two or four core CI runner is most of them.
    $throttle = [Math]::Max(1, [Math]::Min($selected.Count, [Environment]::ProcessorCount))
}

if ($sequential.IsPresent) {
    $results = @($selected | ForEach-Object {
        Invoke-TestTargetShard -Context $context -Name $_.Name -Project $_.Value
    })
} else {
    $module = "$PSScriptRoot/common.psm1"
    $results = @($selected | ForEach-Object -ThrottleLimit $throttle -Parallel {
        Import-Module $using:module -Force
        Invoke-TestTargetShard -Context $using:context -Name $_.Name -Project $_.Value
    })
}

# Held until every shard is done and printed one block at a time. Concurrent targets writing to a
# single console interleave into something that cannot be read, least of all by whoever is looking
# for which target failed.
foreach ($result in ($results | Sort-Object Target)) {
    Write-Comment -prefix "..." -text "----- $($result.Target) [$($result.Stage)] -----" -color "blue"
    if (-not [string]::IsNullOrWhiteSpace($result.Output)) {
        Write-Host $result.Output
    }
}

$outcome = Test-ShardOutcome -Results $results -ExpectedTargets $expected_targets
if ($outcome.IsFailed) {
    foreach ($message in $outcome.Messages) {
        Write-Error $message
    }

    # Only when something never ran. A test that ran and failed is not fixed by building again, and
    # saying so in front of the failure sends whoever is reading it to the wrong place.
    if ($outcome.HasUnrunTargets) {
        Write-Error "Build that configuration first, for example: ./Scripts/build.ps1 -configuration $configuration"
        if ($ci.IsPresent) {
            Write-Error "In CI the '$configuration' build must produce at least one target framework."
        } else {
            Write-Error "Check the '$configuration' build has '$framework' output, or pass a -framework that was built."
        }
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
