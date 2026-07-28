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

$exemptions = @(& "$PSScriptRoot/check-build-layout.ps1" -listExemptions)
Assert-That ($LASTEXITCODE -eq 0) "Layout exemptions: could not list the exemptions"
Assert-That ($exemptions.Count -gt 0) "Layout exemptions: found none to check"

Push-Location $RootDir
try {
    foreach ($exemption in $exemptions) {
        & git ls-files --error-unmatch -- $exemption 2>$null | Out-Null
        Assert-That ($LASTEXITCODE -eq 0) `
            "Layout exemptions: '$exemption' is exempted by check-build-layout.ps1 but is not a tracked file"
    }
}
finally {
    Pop-Location
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
