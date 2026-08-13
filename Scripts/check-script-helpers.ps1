# Copyright (c) 2026 pipflow.com <https://pipflow.com>
#
# This file is part of InterleaveX and is licensed under the GNU General
# Public License v3.0 or later. See LICENSE-GPL for the full text.

<#
.SYNOPSIS
Asserts the decisions the build and benchmark scripts rely on.

.DESCRIPTION
The scripts under Scripts/ decide a handful of things that are easy to get subtly wrong and whose
failure is silent: whether a path is inside the repository, how to invoke a tool on this platform,
and whether a run actually produced anything. Each of those is a function in common.psm1 so that it
can be stated once and checked here, rather than being written out again in every caller and drifting.

Follows the pattern of check-build-layout.ps1: no test framework is involved, every failure is
printed, and the exit code carries the result.
#>

$ErrorActionPreference = "Stop"
$RootDir = (Resolve-Path "$PSScriptRoot/..").Path
Import-Module $PSScriptRoot/common.psm1 -Force

& $PSScriptRoot/check-ilverify-diagnostics.ps1
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$failures = New-Object System.Collections.ArrayList

function Assert-That($condition, $description) {
    if (-not $condition) {
        [void]$failures.Add($description)
    }
}

function Assert-Equal($expected, $actual, $description) {
    if ($expected -ne $actual) {
        [void]$failures.Add("$description (expected '$expected', got '$actual')")
    }
}

# Runs the specified action against a directory that exists only for the duration of the action.
function Invoke-InTemporaryDirectory($action) {
    $path = Join-Path (Get-TempDirectory) ("interleavex-script-helpers-" + [Guid]::NewGuid().ToString("N"))
    New-Item -Path $path -ItemType Directory -Force | Out-Null
    try {
        & $action $path
    }
    finally {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Comment -prefix "." -text "Checking the script helpers" -color "yellow"

# --- Test-PathIsUnder -------------------------------------------------------------------------
#
# A sibling directory whose name merely starts with the repository's name is outside it. Comparing
# raw prefixes says otherwise, which rejected a perfectly good output directory.

$sibling = "$RootDir-results"
Assert-That (-not (Test-PathIsUnder $RootDir $sibling)) `
    "Test-PathIsUnder: a sibling sharing a name prefix ('$sibling') must not be under '$RootDir'"

Assert-That (Test-PathIsUnder $RootDir (Join-Path $RootDir "Scripts")) `
    "Test-PathIsUnder: a direct child must be under the root"

Assert-That (Test-PathIsUnder $RootDir (Join-Path $RootDir "Scripts/CI/azure-nuget-sign-publish.yml")) `
    "Test-PathIsUnder: a nested descendant must be under the root"

Assert-That (-not (Test-PathIsUnder $RootDir $RootDir)) `
    "Test-PathIsUnder: the root is not under itself"

Assert-That (Test-PathIsUnder "$RootDir$([IO.Path]::DirectorySeparatorChar)" (Join-Path $RootDir "Scripts")) `
    "Test-PathIsUnder: a trailing separator on the root must not change the answer"

Assert-That (-not (Test-PathIsUnder $RootDir (Split-Path $RootDir -Parent))) `
    "Test-PathIsUnder: the parent directory is not under the root"

# --- Get-BenchmarkRunnerCommand ---------------------------------------------------------------
#
# The runner is an Exe project, so its apphost is 'BenchmarkRunner.exe' on Windows and extensionless
# elsewhere. Running the DLL through the dotnet host is the one form that works on every platform,
# and is what the runner's own post-build steps do for the CLI.

$runner = Get-BenchmarkRunnerCommand "/some/benchmarks/dir"
Assert-Equal "dotnet" $runner.Tool "Get-BenchmarkRunnerCommand: must invoke the dotnet host"
Assert-That ($runner.Prefix -like "*BenchmarkRunner.dll*") `
    "Get-BenchmarkRunnerCommand: must run BenchmarkRunner.dll, got '$($runner.Prefix)'"
Assert-That ($runner.Prefix -notlike "*.exe*") `
    "Get-BenchmarkRunnerCommand: must not name a Windows-only apphost, got '$($runner.Prefix)'"

# --- Test-BenchmarksProduced ------------------------------------------------------------------
#
# The runner creates its output directory while parsing arguments, before a single benchmark runs, so
# the directory existing says only that the process started. BenchmarkDotNet writes its per-benchmark
# report into a 'results' subdirectory, and that appears only once measurements exist.

Invoke-InTemporaryDirectory {
    param($dir)

    Assert-That (-not (Test-BenchmarksProduced $dir)) `
        "Test-BenchmarksProduced: an empty directory means the runner failed before benchmarking"

    New-Item -Path (Join-Path $dir "results") -ItemType Directory -Force | Out-Null
    Assert-That (-not (Test-BenchmarksProduced $dir)) `
        "Test-BenchmarksProduced: an empty 'results' directory is still not a measurement"

    Set-Content -Path (Join-Path $dir "results/Some.Benchmark-report.csv") -Value "Method,Mean" -Encoding utf8
    Assert-That (Test-BenchmarksProduced $dir) `
        "Test-BenchmarksProduced: a report file means benchmarks ran"
}

Assert-That (-not (Test-BenchmarksProduced (Join-Path (Get-TempDirectory) ([Guid]::NewGuid().ToString("N"))))) `
    "Test-BenchmarksProduced: a directory that does not exist is not a measurement"

# --- Get-TempDirectory ------------------------------------------------------------------------
#
# $ENV:TEMP is unset on Linux and macOS, where it would silently yield a relative path inside the
# repository -- which is the one place the benchmark history script must not write.

$temp = Get-TempDirectory
Assert-That (-not [string]::IsNullOrEmpty($temp)) "Get-TempDirectory: must return a path"
Assert-That ([IO.Path]::IsPathRooted($temp)) "Get-TempDirectory: must return an absolute path, got '$temp'"
Assert-That (-not (Test-PathIsUnder $RootDir $temp)) "Get-TempDirectory: must be outside the repository"

# --- The benchmark snapshot covers every benchmark project ------------------------------------
#
# The history script restores its own copy of the benchmark sources over each commit so that every
# commit is measured by the same code. A benchmark project it forgets to snapshot stays
# commit-specific, and a change to that benchmark then reads as a runtime regression. Six of the
# eight benchmarks came from a project the snapshot did not list.

$runnerProject = Join-Path $RootDir "Tools/BenchmarkRunner/BenchmarkRunner.csproj"
$snapshotted = (& "$PSScriptRoot/run-benchmark-history.ps1" -listSnapshotPaths) |
    ForEach-Object { $_.Replace("\", "/").TrimEnd("/") }

$referenced = Select-String -Path $runnerProject -Pattern 'ProjectReference Include="([^"]+)"' -AllMatches |
    ForEach-Object { $_.Matches } |
    ForEach-Object { $_.Groups[1].Value.Replace("\", "/") } |
    Where-Object { $_ -like "*Tests/*" } |
    ForEach-Object { (Split-Path $_ -Parent).Replace("\", "/").Replace("../../", "") }

foreach ($project in $referenced) {
    Assert-That ($snapshotted -contains $project) `
        "Benchmark snapshot: '$project' is referenced by the runner but is not snapshotted"
}

Assert-That ($referenced.Count -gt 0) "Benchmark snapshot: found no benchmark project references to check"

# --- Every layout exemption still names a file ------------------------------------------------
#
# check-build-layout.ps1 excuses individual lines by file and text, and skips itself outright because
# every path it contains is an example. Both kinds go stale silently when a file is renamed: the
# exemption stays in the table, matches nothing, and the checker keeps passing. The text half is
# caught by the checker itself, which reports an exemption that matched nothing; this catches the
# half that is about the file. 'git ls-files' is used rather than the file system because CI checks
# out shallow, and because an exemption for an untracked file could never fire in the first place.

# The layout checker reads tracked files, so every exemption must name one.
$checker = "check-build-layout.ps1"
$exemptions = @(& "$PSScriptRoot/$checker" -listExemptions)
Assert-That ($LASTEXITCODE -eq 0) "Exemptions: could not list those of $checker"
Assert-That ($exemptions.Count -gt 0) "Exemptions: $checker listed none to check"

Push-Location $RootDir
try {
    foreach ($exemption in $exemptions) {
        & git ls-files --error-unmatch -- $exemption 2>$null | Out-Null
        Assert-That ($LASTEXITCODE -eq 0) `
            "Exemptions: '$exemption' is exempted by $checker but is not a tracked file"
    }
}
finally {
    Pop-Location
}

# --- Invoke-ToolCommandWithResult ---------------------------------------------------------------
#
# Every other failure path in common.psm1 reports itself by calling 'exit' in the caller's scope.
# That works only while the caller is the script. Once the targets are run together, each one is a
# separate runspace, and 'exit' there ends the runspace alone while the parent finishes and reports
# success -- a failing test run that passes. Verified directly below, because it is the entire
# reason this function exists.

$hazard = @(1) | ForEach-Object -Parallel { exit 7 }
Assert-That ($hazard.Count -eq 0) `
    "Parallel hazard: 'exit' in a runspace must produce no result (got $($hazard.Count)); the aggregation relies on absence being detectable"

# Writes a script that ignores its arguments and exits with the specified code, and returns the tool
# string that runs it. A real process is used because $LASTEXITCODE is set by native commands only.
function New-FakeTool($dir, $name, $code, $message = "fake tool output") {
    $path = Join-Path $dir "$name.ps1"
    Set-Content -Path $path -Value "Write-Output '$message'`nexit $code" -Encoding utf8
    return "pwsh -NoProfile -File `"$path`""
}

Invoke-InTemporaryDirectory {
    param($dir)

    $failing = New-FakeTool $dir "failing" 3 "it went wrong"
    $result = Invoke-ToolCommandWithResult -tool $failing -cmd "some args"
    Assert-Equal 3 $result.ExitCode "Invoke-ToolCommandWithResult: must return the tool's exit code rather than exiting"
    Assert-That ($result.Output -like "*it went wrong*") `
        "Invoke-ToolCommandWithResult: must capture what the tool wrote, got '$($result.Output)'"

    $passing = New-FakeTool $dir "passing" 0
    Assert-Equal 0 (Invoke-ToolCommandWithResult -tool $passing -cmd "").ExitCode `
        "Invoke-ToolCommandWithResult: a successful tool must report zero"
}

# --- Invoke-TestTargetShard -------------------------------------------------------------------
#
# The shard is what turns 'this target did not work' into a value the parent can act on. Each stage
# is checked against a fake repository, with the tools replaced by scripts that fail on demand: what
# is under test is the plumbing from a nonzero exit code to a reported stage, not whether the real
# ilverify or xunit can fail. Those can, and proving it again here would cost minutes and show
# nothing about the accounting that was actually broken.

# Runs the specified body against a fake repository laid out the way the shard expects, containing
# one target built for the given frameworks.
#
# The parameter is named '$body' rather than '$action' deliberately. The scriptblock below runs in
# the scope of Invoke-InTemporaryDirectory, whose own parameter is '$action', so a name collision
# there resolves to that scriptblock instead of to this one and calls itself until the call depth
# runs out.
function Invoke-InFakeRepository($frameworks, $body) {
    Invoke-InTemporaryDirectory {
        param($dir)

        $scripts = Join-Path $dir "Scripts"
        $project = Join-Path $dir "Tests/Fake"
        New-Item -Path $scripts -ItemType Directory -Force | Out-Null
        New-Item -Path $project -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $project "Fake.csproj") -Encoding utf8 `
            -Value "<Project><PropertyGroup><AssemblyName>Fake</AssemblyName></PropertyGroup></Project>"
        foreach ($f in $frameworks) {
            New-Item -Path (Join-Path $project "bin/Release/$f") -ItemType Directory -Force | Out-Null
        }

        & $body $dir $scripts
    }
}

# Returns a context for the fake repository, with both tools exiting with the given codes.
function New-FakeContext($dir, $scripts, $framework, $ilverifyCode, $dotnetCode) {
    return @{
        ScriptRoot = $scripts
        Configuration = "Release"
        Framework = $framework
        AllFrameworks = @("net10.0", "net9.0", "net8.0", "net6.0", "net462")
        IsCi = $false
        Filter = ""
        Logger = ""
        Verbosity = "normal"
        Dotnet = (New-FakeTool $dir "dotnet" $dotnetCode)
        Ilverify = (New-FakeTool $dir "ilverify" $ilverifyCode)
        DotnetRuntimePath = $dir
        AspnetRuntimePath = $dir
        RuntimeVersion = "10.0.0"
    }
}

# A target nobody built is reported as such, rather than as a target with nothing to do.
Invoke-InFakeRepository @() {
    param($dir, $scripts)

    $context = New-FakeContext $dir $scripts "net10.0" 0 0
    $shard = Invoke-TestTargetShard -Context $context -Name "fake" -Project "Missing"
    Assert-Equal "unbuilt" $shard.Stage "Invoke-TestTargetShard: a target with no build output is 'unbuilt'"
    Assert-That ($shard.ExitCode -ne 0) "Invoke-TestTargetShard: an unbuilt target must not report success"
}

# A '-framework' nobody built is filtered out in silence, so the shard has to notice it ran nothing.
Invoke-InFakeRepository @("net8.0") {
    param($dir, $scripts)

    $context = New-FakeContext $dir $scripts "net10.0" 0 0
    $shard = Invoke-TestTargetShard -Context $context -Name "fake" -Project "Fake"
    Assert-Equal "empty" $shard.Stage "Invoke-TestTargetShard: a target that ran no framework is 'empty'"
    Assert-That ($shard.ExitCode -ne 0) "Invoke-TestTargetShard: a target that ran nothing must not report success"
}

# Verification failing is a corrupted rewrite, and must stop that target before its tests run.
Invoke-InFakeRepository @("net10.0") {
    param($dir, $scripts)

    $context = New-FakeContext $dir $scripts "net10.0" 5 0
    $shard = Invoke-TestTargetShard -Context $context -Name "fake" -Project "Fake"
    Assert-Equal "ilverify" $shard.Stage "Invoke-TestTargetShard: a failing verification is reported at the 'ilverify' stage"
    Assert-Equal 5 $shard.ExitCode "Invoke-TestTargetShard: must carry the verifier's exit code"
}

# 'net6.0' skips verification, which isolates the test stage from it.
Invoke-InFakeRepository @("net6.0") {
    param($dir, $scripts)

    $context = New-FakeContext $dir $scripts "net6.0" 0 1
    $shard = Invoke-TestTargetShard -Context $context -Name "fake" -Project "Fake"
    Assert-Equal "test" $shard.Stage "Invoke-TestTargetShard: a failing test run is reported at the 'test' stage"
    Assert-Equal 1 $shard.ExitCode "Invoke-TestTargetShard: must carry the test runner's exit code"

    $context = New-FakeContext $dir $scripts "net6.0" 0 0
    $shard = Invoke-TestTargetShard -Context $context -Name "fake" -Project "Fake"
    Assert-Equal "ok" $shard.Stage "Invoke-TestTargetShard: a target whose tests passed is 'ok'"
    Assert-Equal 0 $shard.ExitCode "Invoke-TestTargetShard: a passing target reports zero"
    Assert-That ($shard.Frameworks -contains "net6.0") `
        "Invoke-TestTargetShard: must report which frameworks actually ran, got '$($shard.Frameworks -join ',')'"
}

# --- Test-ShardOutcome ------------------------------------------------------------------------
#
# The verdict both dispatch modes reach. Every way a run can fail is checked here rather than by
# running the suite, so that adding a mode cannot quietly add a way to pass.

function New-Shard($target, $stage, $code) {
    return [pscustomobject]@{
        Target = $target; Name = $target; Frameworks = @(); Stage = $stage; ExitCode = $code; Output = ""
    }
}

$passed = New-Shard "A" "ok" 0
Assert-That (-not (Test-ShardOutcome -Results @($passed, (New-Shard "B" "ok" 0)) -ExpectedTargets @("A", "B")).IsFailed) `
    "Test-ShardOutcome: every target passing is a passing run"

foreach ($case in @(
        @{ Stage = "test"; Code = 1; Why = "a failing test run"; Unrun = $false },
        @{ Stage = "ilverify"; Code = 2; Why = "a failing verification"; Unrun = $false },
        @{ Stage = "unbuilt"; Code = 1; Why = "a target nobody built"; Unrun = $true },
        @{ Stage = "empty"; Code = 1; Why = "a target that ran nothing"; Unrun = $true })) {
    $outcome = Test-ShardOutcome -Results @($passed, (New-Shard "B" $case.Stage $case.Code)) -ExpectedTargets @("A", "B")
    Assert-That $outcome.IsFailed "Test-ShardOutcome: $($case.Why) must fail the run"
    Assert-That (($outcome.Messages -join " ") -like "*B*") `
        "Test-ShardOutcome: $($case.Why) must name the target, got '$($outcome.Messages -join ' | ')'"

    # What decides whether the run is told to build first. Advice about the build in front of a test
    # that ran and failed sends whoever is reading it somewhere the failure is not.
    Assert-Equal $case.Unrun $outcome.HasUnrunTargets `
        "Test-ShardOutcome: $($case.Why) must $(if ($case.Unrun) { '' } else { 'not ' })be a target that ran nothing"
}

# The case the fan-out introduced: a shard that threw or crashed contributes no result at all, and
# counting only what came back would read that as a clean run.
$outcome = Test-ShardOutcome -Results @($passed) -ExpectedTargets @("A", "B")
Assert-That $outcome.IsFailed "Test-ShardOutcome: a target that reported no result at all must fail the run"
Assert-That (-not $outcome.HasUnrunTargets) `
    "Test-ShardOutcome: a shard that crashed is not a missing build, so it must not ask for one"

Assert-That (Test-ShardOutcome -Results @($passed, $null) -ExpectedTargets @("A", "B")).IsFailed `
    "Test-ShardOutcome: a null result must not satisfy the target it was expected for"

Assert-That (Test-ShardOutcome -Results @() -ExpectedTargets @("A")).IsFailed `
    "Test-ShardOutcome: no results at all must fail the run"

# --- Both dispatch modes fail the run identically ---------------------------------------------
#
# The two modes differ only in how the shards are dispatched, so anything else that differs is a
# bug. Driven through run-tests.ps1 itself rather than through the functions it calls, because what
# is under test here is that script's own wiring: that it reaches the verdict, prints what led to it
# and carries it out to an exit code, identically either way. That is the bug this began as -- a run
# with nothing to do reaching 'Done' with exit code 0.
#
# Against a copy of the scripts laid beside a fake tree, not against this repository. '$PSScriptRoot'
# inside the child resolves to wherever the file it is running lives, so copying run-tests.ps1 and
# common.psm1 into the fake 'Scripts' directory points every path they derive at the fake tree. Run
# against the real one, the answer would depend on what happens to be built: this gate runs before
# the build step in CI, where nothing is, and again after it through 'run-tests.ps1 -ci', where on
# Windows 'net462' is -- at which point asking for 'net462' does not test nothing, it runs the whole
# Tools suite. Both of those were live: the first reported the wrong stage, the second passed the
# run and failed the assertion.
#
# Nothing is built in the fake tree either, so no test can run however this is invoked.

# Runs run-tests.ps1 against a fake repository holding one target built for the given frameworks, and
# returns what it wrote and the code it exited with.
function Invoke-RunTestsInFakeRepository($project, $frameworks, $arguments) {
    # Returned as the value of the scriptblock rather than assigned to a variable out here. '&' runs
    # it in a child scope, so an assignment inside would create a variable there and leave this one
    # null -- silently, and looking exactly like a run that produced nothing.
    return Invoke-InTemporaryDirectory {
        param($dir)

        $scripts = Join-Path $dir "Scripts"
        New-Item -Path $scripts -ItemType Directory -Force | Out-Null
        Copy-Item -Path "$PSScriptRoot/run-tests.ps1", "$PSScriptRoot/common.psm1" -Destination $scripts

        # 'FindDotNetRuntimeVersion' reads this from beside the scripts. Copied rather than left out
        # so that the child does not print a missing-file error over the output being asserted on.
        Copy-Item -Path "$PSScriptRoot/../global.json" -Destination $dir

        $target = Join-Path $dir "Tests/$project"
        New-Item -Path $target -ItemType Directory -Force | Out-Null
        Set-Content -Path (Join-Path $target "$project.csproj") -Encoding utf8 `
            -Value "<Project><PropertyGroup><AssemblyName>$project</AssemblyName></PropertyGroup></Project>"
        foreach ($f in $frameworks) {
            New-Item -Path (Join-Path $target "bin/Release/$f") -ItemType Directory -Force | Out-Null
        }

        $output = & pwsh -NoProfile -File "$scripts/run-tests.ps1" @arguments 2>&1 | Out-String
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
}

# The two ways a target contributes no test run, one each. 'Tests.Tools' is built, but for a
# framework other than the one asked for, which is filtered out in silence. 'Tests.Runtime' is not in
# the fake tree at all, which is only a non-terminating Get-ChildItem error.
$cases = @(
    @{ Project = "Tests.Tools"; Frameworks = @("net8.0"); Test = "tools"
       Says = "no tests ran for"; Why = "a target that ran no framework" },
    @{ Project = "Tests.Tools"; Frameworks = @(); Test = "runtime"
       Says = "no build found for"; Why = "a target nobody built" }
)

foreach ($case in $cases) {
    $modes = @{}
    foreach ($mode in @("sequential", "parallel")) {
        $arguments = @("-framework", "net462", "-test", $case.Test, "-noSetup", "-noGates")
        if ($mode -eq "sequential") {
            $arguments += "-sequential"
        }

        $modes[$mode] = Invoke-RunTestsInFakeRepository $case.Project $case.Frameworks $arguments
    }

    foreach ($mode in @("sequential", "parallel")) {
        $run = $modes[$mode]
        Assert-That ($run.ExitCode -ne 0) `
            "run-tests.ps1 ($mode): $($case.Why) must fail the run, got exit $($run.ExitCode)"
        Assert-That ($run.Output -like "*$($case.Says)*") `
            "run-tests.ps1 ($mode): $($case.Why) must be reported as '$($case.Says)', got '$($run.Output)'"

        # The one case the advice about building first is for. That it is printed here and withheld
        # from an ordinary failing test is the whole of the distinction.
        Assert-That ($run.Output -like "*Build that configuration first*") `
            "run-tests.ps1 ($mode): $($case.Why) must say to build first"

        Assert-That ($run.Output -notlike "*Testing '*") `
            "run-tests.ps1 ($mode): $($case.Why) must not have run a test, got '$($run.Output)'"
    }

    Assert-Equal $modes["sequential"].ExitCode $modes["parallel"].ExitCode `
        "run-tests.ps1: both modes must reach the same exit code for $($case.Why)"
}

# --- Result -----------------------------------------------------------------------------------

if ($failures.Count -eq 0) {
    Write-Comment -prefix "." -text "The script helpers behave as expected." -color "green"
    exit 0
}

Write-Comment -prefix "." -text "$($failures.Count) script helper check(s) failed." -color "red"
foreach ($failure in $failures) {
    Write-Host ""
    Write-Host "  $failure" -ForegroundColor yellow
}

Write-Host ""
exit 1
