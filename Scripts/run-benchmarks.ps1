# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Modifications are licensed under the GNU General Public License v3.0 or
# later. See LICENSE-GPL for the full text.

param(
    [string]$store = "",
    [string]$key = "",
    [string]$local = "",
    [ValidateSet("Debug", "Release")]
    [string]$configuration = "Release"
)

Import-Module $PSScriptRoot/common.psm1 -Force

$history = Invoke-Expression "git log --pretty=oneline -n 1"
$words = $history.Split(' ')
$commit = $words[0]
$cosmos = ""

if ($store -ne "") {
    $env:AZURE_COSMOSDB_ENDPOINT = $store
    $env:AZURE_STORAGE_PRIMARY_KEY = $key
}

# Both are required: the uploader needs the endpoint and the key, and silently does nothing if either
# is missing. Comparing against the empty string is not the test to make -- an unset variable is
# $null, and $null -ne "" is true, so this used to ask for an upload it could never perform.
if ((-not [string]::IsNullOrEmpty($env:AZURE_COSMOSDB_ENDPOINT)) -and
    (-not [string]::IsNullOrEmpty($env:AZURE_STORAGE_PRIMARY_KEY)))
{
    Write-Host "Results will be saved to $ENV:AZURE_COSMOSDB_ENDPOINT"
    $cosmos = " -cosmos"
}

if ($local -eq ""){
    $local = $Env:LocalBenchmarks
}

$current_dir = (Get-Item -Path "./").FullName
$benchmarks_dir = "$PSScriptRoot/../Tools/BenchmarkRunner/bin/$configuration/net8.0"
$artifacts_dir = "$current_dir/benchmark_$commit"

$runner = Get-BenchmarkRunnerCommand $benchmarks_dir
if (-Not (Test-Path -Path $runner.Assembly)) {
    throw "Please build the InterleaveX project first"
}

$custom = "D:/git/lovettchris/BenchmarkDotNet/src/BenchmarkDotNet/bin/Release/netstandard2.0"
if (Test-Path -Path $custom) {
    Write-Host "==> Using a patched version of BenchmarkDotNet..."
    Copy-Item "$custom/BenchmarkDotNet.dll" "$benchmarks_dir/BenchmarkDotNet.dll" -Force
    Copy-Item "$custom/BenchmarkDotNet.Annotations.dll" "$benchmarks_dir/BenchmarkDotNet.Annotations.dll" -Force
}

if (Test-Path -Path $artifacts_dir -PathType Container) {
    Remove-Item $artifacts_dir -Recurse
}

Write-Comment -prefix "." -text "Running the InterleaveX performance benchmarks, saving to $artifacts_dir" -color "yellow"

Invoke-ToolCommand -tool $runner.Tool `
    -cmd "$($runner.Prefix) -outdir `"$artifacts_dir`" -commit $commit$cosmos" `
    -error_msg "The benchmarks failed"

# The runner creates its output directory while parsing arguments, before benchmarking, so the
# directory alone does not mean anything ran.
if (-not (Test-BenchmarksProduced $artifacts_dir)) {
    Write-Comment -prefix "." -text "The benchmarks produced no results ($artifacts_dir)." -color "red"
    exit 1
}

Write-Comment -prefix "." -text "Done" -color "green"

if ($local -ne "") {
    # save the detailed perf results on the test machine with additional integer index to
    # disambiguate duplicate runs for the same commit id.
    if (-not (Test-Path -Path $local)) {
        New-Item -Path $local -ItemType Directory
    }
    $index = 1
    while (Test-Path -Path "$local/benchmark_$commit.$index") {
        $index = $index + 1
    }

    Move-Item -Path $artifacts_dir -Destination "$local/benchmark_$commit.$index"
}
