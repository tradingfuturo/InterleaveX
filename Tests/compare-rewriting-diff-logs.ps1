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
#
# 'testing' rebaselined again with the incremental rewriting work. Its recorded value had gone stale
# the same way, and for a reason now fixed: rewriting in place consumes the original assembly, so a
# rewriter that found one already rewritten wrote no diff at all and left whatever file was there.
# The check then compared an artifact that no recent build had produced. The value below was
# reproduced five times, including from a clean 'obj' and 'bin', and is unchanged by the incremental
# work itself: rewriting a staged copy of the compiler output was confirmed to emit the same IL as
# rewriting the assembly in 'bin', by running both over identical input and comparing.
#
# 'rewriting' rebaselined once more, and only that one: the four other values below were reproduced
# unchanged after every remaining project moved from rewriting 'bin' in place to rewriting a staged
# copy, which is the evidence that the move emits identical IL. This value moved because the change
# that ships with it adds a test method to the project, and a new method is new IL to diff. It was
# reproduced from a deleted 'obj' and 'bin'.
$expected_hashes = [ordered]@{
    "rewriting" = "3FF83EDC9D83423222FFB1C0109B84D3F7AA7C9E20A6D0CDD24EA8E2FA15DA25"
    "rewriting-helpers" = "6C25B8F64593309BD37E258A2C59683FB56B63B281AA20A9B41015D2BFDD2D85"
    "testing" = "22E7EB0DCE8F4CBB99906B9C6C35714F9B01A203D9AB64ECC645570CF467F1A5"
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
