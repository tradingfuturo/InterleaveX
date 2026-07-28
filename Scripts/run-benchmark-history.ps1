# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Modifications are licensed under the GNU General Public License v3.0 or
# later. See LICENSE-GPL for the full text.

<#
.SYNOPSIS
Benchmarks a range of commits with the benchmark sources that are currently checked out.

.DESCRIPTION
Each commit is checked out in turn and built, with the current benchmark sources copied over it, so
that every commit is measured by the same benchmark code and the numbers stay comparable. The sources
held constant are listed in $BenchmarkPaths and must cover every benchmark project the runner
executes; only the files git tracks are copied, so no build output travels between commits.

The range stops at the fork point. Restoring current benchmark sources over an older checkout
cannot work across the Coyote to InterleaveX rebrand, because the projects, assemblies and CLI they
reference were renamed there, so only the fork's own history can be measured this way.

This script rewrites the working tree. It refuses to start unless the tree is clean, and it restores
the branch or commit it started from when it finishes, including after a failure.
#>

param(
    # The filter passed through to same arg on BenchmarkRunner.
    [string]$filter = "",
    # The maximum number of commits, or 0 for every commit in range.
    [int]$max = 0,
    [ValidateSet("Debug", "Release")]
    [string]$configuration = "Release",
    # Where to write the per-commit benchmark results. Must be outside this repository: the working
    # tree is reset between commits, and results written into it would be in the way of that.
    [string]$outdir = "",
    # Uploads the results to the shared benchmark database. Off by default: the upload needs
    # AZURE_COSMOSDB_ENDPOINT and AZURE_STORAGE_PRIMARY_KEY, and it also makes the runner read and
    # parse the git log once per commit, which is pointless work for a local comparison.
    [switch]$cosmos,
    # Prints the benchmark sources this script holds constant across commits, and exits. Used by
    # check-script-helpers.ps1 to confirm the set still covers every benchmark the runner runs.
    [switch]$listSnapshotPaths
)

# The sources held constant across every commit measured, so that a change to a benchmark cannot read
# as a runtime regression. This must cover every project under Tests/ that BenchmarkRunner.csproj
# references, which check-script-helpers.ps1 asserts.
$BenchmarkPaths = @(
    "Tests/Tests.Performance",
    "Tests/Tests.Actors.Performance",
    "Tools/BenchmarkRunner"
)

if ($listSnapshotPaths.IsPresent) {
    $BenchmarkPaths
    exit 0
}

$ScriptDir = $PSScriptRoot
$RootDir = (Resolve-Path "$ScriptDir/..").Path
Import-Module $ScriptDir/common.psm1 -Force
CheckPSVersion

# The commit that established the fork. Anything before it names the pre-rebrand projects, which the
# restored benchmark sources no longer reference.
$ForkPoint = "b4132764"

Set-Location -Path $RootDir

$dirty = & git status --porcelain
if ($dirty) {
    Write-Comment -prefix "." -text "The working tree has uncommitted changes." -color "red"
    Write-Comment -prefix "." -text "This script runs 'git reset --hard' and 'git checkout' in a loop, which would discard them." -color "red"
    Write-Comment -prefix "." -text "Commit or stash first." -color "red"
    exit 1
}

# Remember where to put the repository back. A detached HEAD has no branch name to return to, so
# fall back to the commit itself.
$startingRef = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($startingRef -eq "HEAD") {
    $startingRef = (& git rev-parse HEAD).Trim()
}

# Copy the benchmark sources somewhere unique, so that a run cannot pick up what an interrupted
# earlier run left behind, and somewhere outside the tree that is about to be reset.
$source = Join-Path (Get-TempDirectory) "InterleaveX-Benchmark-$([Guid]::NewGuid().ToString('N'))"
if ($outdir -eq "") {
    $outdir = Join-Path (Get-TempDirectory) "InterleaveX-BenchmarkResults"
}

$outdir = [IO.Path]::GetFullPath($outdir)
if ((Test-PathIsUnder $RootDir $outdir) -or ($outdir.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $RootDir)) {
    Write-Comment -prefix "." -text "The output directory must be outside the repository, which is reset between commits." -color "red"
    exit 1
}

# net8.0 is the common denominator across the whole measurable range, and what the performance
# projects are built against; the runner also builds net10.0 and net9.0.
$benchmarks_dir = "$RootDir/Tools/BenchmarkRunner/bin/$configuration/net8.0"
$index = 0

# Copies the snapshot back over the checked out commit, so that every commit is measured by the same
# benchmark code.
function RestoreBenchmark() {
    Write-Comment -prefix "..." -text "Restoring latest benchmark source code"
    foreach ($path in $BenchmarkPaths) {
        $target = Join-Path $RootDir $path
        if (Test-Path $target) {
            Remove-Item $target -Recurse -Force
        }

        Copy-Item (Join-Path $source $path) -Recurse $target
    }
}

function ProcessCommit($commit) {
    Write-Comment -prefix "." -text "Checking out $commit" -color "yellow"
    Invoke-ToolCommand -tool "git" -cmd "reset --hard" -error_msg "Failed to reset the working tree"
    Invoke-ToolCommand -tool "git" -cmd "checkout --detach $commit" -error_msg "Failed to check out $commit"
    RestoreBenchmark

    # Build the benchmark runner rather than the solution. Its project references pull in the
    # performance tests and the CLI, so the solution file of the checked out commit, which names the
    # projects as they were called then, never has to agree with the restored sources.
    Invoke-ToolCommand -tool "dotnet" `
        -cmd "build -c $configuration $RootDir/Tools/BenchmarkRunner/BenchmarkRunner.csproj" `
        -error_msg "Failed to build the benchmark runner at $commit"
    Start-Sleep -Seconds 5
    Invoke-ToolCommand -tool "dotnet" -cmd "build-server shutdown" -error_msg "Failed to shut down the build server"
    Start-Sleep -Seconds 5

    $runner = Get-BenchmarkRunnerCommand $benchmarks_dir
    if (-not (Test-Path $runner.Assembly)) {
        # Checked rather than inferred from the exit code: a command that fails to launch leaves
        # $LASTEXITCODE holding whatever the previous one set, which can read as success.
        Write-Comment -prefix "." -text "The benchmark runner was not built ($($runner.Assembly))." -color "red"
        exit 1
    }

    $artifacts_dir = Join-Path $outdir "benchmark_$commit"
    $upload = ""
    if ($cosmos.IsPresent) {
        $upload = " -cosmos"
    }

    Invoke-ToolCommand -tool $runner.Tool `
        -cmd "$($runner.Prefix) -outdir `"$artifacts_dir`" -commit $commit$upload $filter" `
        -error_msg "The benchmarks failed at $commit"

    # The output directory is created while the runner parses its arguments, before a single
    # benchmark executes, so its existence says only that the process started. A report file appears
    # only once measurements exist.
    if (-not (Test-BenchmarksProduced $artifacts_dir)) {
        Write-Comment -prefix "." -text "The benchmarks produced no results at $commit ($artifacts_dir)." -color "red"
        exit 1
    }
}

try {
    New-Item -Path $source -ItemType Directory -Force | Out-Null
    New-Item -Path $outdir -ItemType Directory -Force | Out-Null

    Write-Comment -prefix "." -text "Saving current benchmark source code to $source" -color "yellow"

    # Snapshot only what git tracks. A recursive copy would take 'bin' and 'obj' with it and restore
    # them into every commit measured, where a build can reuse them instead of rebuilding the
    # historical dependency graph; 'git reset --hard' does not remove ignored files, so they would
    # then persist for the rest of the run. Listing tracked files excludes anything ignored now or
    # ignored later, rather than the two directory names that happen to exist today.
    foreach ($path in $BenchmarkPaths) {
        $tracked = & git ls-files -- $path
        if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {
            Write-Comment -prefix "." -text "No tracked files under '$path'." -color "red"
            exit 1
        }

        foreach ($file in $tracked) {
            $destination = Join-Path $source $file
            New-Item -Path (Split-Path $destination -Parent) -ItemType Directory -Force | Out-Null
            Copy-Item (Join-Path $RootDir $file) $destination
        }
    }

    $history = & git log --first-parent --pretty=oneline "$ForkPoint^..HEAD"
    Write-Comment -prefix "." -text "Benchmarking $($history.Count) commit(s) back to the fork point." -color "yellow"

    foreach ($line in $history) {
        $words = $line.Split(' ')
        $commit = $words[0]
        ProcessCommit $commit
        $index = $index + 1
        if (($max -ne 0) -And ($index -eq $max)) {
            Write-Comment -prefix "." -text "Terminating after max tests: $max" -color "yellow"
            break
        }
    }

    Write-Comment -prefix "." -text "Benchmark results are in $outdir" -color "green"
}
finally {
    Set-Location -Path $RootDir
    Write-Comment -prefix "." -text "Restoring $startingRef" -color "yellow"
    & git reset --hard | Out-Null
    & git checkout $startingRef | Out-Null
    if (Test-Path $source) {
        Remove-Item $source -Recurse -Force
    }
}
