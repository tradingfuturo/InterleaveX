# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Modifications are licensed under the GNU General Public License v3.0 or
# later. See LICENSE-GPL for the full text.

Import-Module $PSScriptRoot/../Scripts/common.psm1 -Force

# Debug and release builds do not emit the same IL, so the hashes below are only meaningful
# for one configuration. Name it explicitly rather than hashing whichever build happens to
# be on disk: this check previously read a configuration-agnostic output directory, so the
# result depended on which configuration had been built last. Release is what build.ps1 and
# CI produce.
$configuration = "Release"
$framework = "net8.0"
$targets = [ordered]@{
    "rewriting" = "Tests.Rewriting"
    "rewriting-helpers" = "Tests.Rewriting.Helpers"
    "testing" = "Tests.BugFinding"
    "actors" = "Tests.Actors"
    "actors-testing" = "Tests.Actors.BugFinding"
}

# Hashes of the release build, which is what build.ps1 and CI produce. Regenerate with
# get-rewriting-diff-logs.ps1 using the same configuration.
$expected_hashes = [ordered]@{
    "rewriting" = "2BAF0F754A273649857D44741546B1DF822DEFD809236720A21FD59331268BAB"
    "rewriting-helpers" = "DF8CF299C162ECA5392793BF5E3E6D7C8B61A75E029501ED6F41C1DD1AD3183B"
    "testing" = "24718D8B9A44CDF215505E9E4B44924950659969D5CA07AABF6D99B023123F68"
    "actors" = "9BC8B815D49CF0D64A815F1492C58B327DAB10B6DE921D6283ED799A367D7AB8"
    "actors-testing" = "29D71EE8298B402FF3477D5EE89639C22B5295027B3C09EE366373DAE37A5D59"
}

Write-Comment -prefix "." -text "Comparing the test rewriting diff logs" -color "yellow"

# Compare all IL diff logs.
$succeeded = $true
foreach ($kvp in $targets.GetEnumerator()) {
    $project = $($kvp.Value)
    if ($project -eq $targets["actors"]) {
        $project = $targets["actors-testing"]
    } elseif ($project -eq $targets["rewriting-helpers"]) {
        $project = $targets["rewriting"]
    }

    $new = "$PSScriptRoot/$project/bin/$configuration/$framework/Microsoft.Coyote.$($kvp.Value).diff.json"
    if (-not (Test-Path $new)) {
        Write-Error "The '$($kvp.Value)' project has no IL diff log at '$new'. Build the $configuration configuration first."
        $succeeded = $false
        continue
    }

    $new_hash = $(Get-FileHash $new).Hash
    Write-Comment -prefix "..." -text "Computed IL diff hash '$new_hash' for '$($kvp.Value)' project"
    $expected_hash = $expected_hashes[$($kvp.Key)]
    if ($new_hash -ne $expected_hash) {
        Write-Error "The '$($kvp.Value)' project's IL diff hash '$new_hash' is not the expected '$expected_hash'."
        $succeeded = $false
    }
}

if (-not $succeeded) {
    exit 1
}

Write-Comment -prefix "." -text "Done" -color "green"
