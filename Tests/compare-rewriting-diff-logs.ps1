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
#
# Rebaselined because the previous values had been stale since 'Redirect every producer of a
# configured awaitable', which changed the IL injected into Tests.BugFinding without regenerating
# them; the rewriter work that followed, on channels, Monitor, Lock and thread pooling, moved the
# rest. Every project below except Tests.BugFinding drifted before the change this rebaseline ships
# with, which only adds a test method. The hashes are reproducible rather than machine specific:
# the previous values were reproduced exactly at the commit that recorded them, which is how the
# first divergence was placed.
$expected_hashes = [ordered]@{
    "rewriting" = "A9016EE75232E1EE719D36F7A38776D09A462B5896B117FBF14C0F759E68857E"
    "rewriting-helpers" = "6C25B8F64593309BD37E258A2C59683FB56B63B281AA20A9B41015D2BFDD2D85"
    "testing" = "4D48FEE90DC5C7D34C151CE7D9BFDADFF806D4D217E99F281682EB54737A1893"
    "actors" = "531E0CC7818CC9B5B23C2DACF1B0DF4570C13BD45DB018E964AEE28E08179FF9"
    "actors-testing" = "8A3F2C008BB108C5C587DECF3205B10817E316D489CB670332119C460460AC4D"
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
