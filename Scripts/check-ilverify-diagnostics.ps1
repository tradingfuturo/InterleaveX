# Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Licensed under the GNU General Public License v3.0 or later.

$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "common.psm1") -Force

$temp = Join-Path ([IO.Path]::GetTempPath()) ("interleavex-ilverify-" + [Guid]::NewGuid().ToString("N"))
New-Item -Path $temp -ItemType Directory | Out-Null
try {
    $tool = Join-Path $temp "fake-ilverify.ps1"
    Set-Content -Path $tool -Encoding utf8 -Value @'
Write-Output '[MD]: Error [Fake.dll : M()] Invalid metadata token'
Write-Output '[IL]: Error [Fake.dll : N()][offset 0x00000001] StackUnexpected'
exit 1
'@
    $context = @{ Ilverify = "pwsh -NoProfile -File `"$tool`"" }
    $result = Invoke-Ilverify -Context $context -Assembly "Fake.dll" -References @()
    if ($result.Errors.Count -ne 2) {
        throw "Invoke-Ilverify parsed $($result.Errors.Count) of 2 categorized diagnostics."
    }

    Set-Content -Path $tool -Encoding utf8 -Value @'
Write-Output '[IL]: Error [Fake.dll : N()][offset 0x00000001] StackUnexpected'
Write-Output 'fatal verifier transport failure'
Write-Output '1 Error(s) Verifying Fake.dll'
exit 1
'@
    $result = Invoke-Ilverify -Context $context -Assembly "Fake.dll" -References @()
    if ($result.Errors.Count -ne 1 -or $result.Unparsed.Count -ne 1 -or
        $result.Unparsed[0] -ne "fatal verifier transport failure") {
        throw "Invoke-Ilverify did not retain an unclassified nonzero failure."
    }

    function New-DiagnosticResult($exitCode, $errors, $unparsed) {
        return [pscustomobject]@{
            ExitCode = $exitCode
            Errors = @($errors)
            Unparsed = @($unparsed)
        }
    }

    $candidate = New-DiagnosticResult 1 @() @("same unparsed failure")
    $baseline = New-DiagnosticResult 1 @() @("same unparsed failure")
    $comparison = Compare-IlverifyDiagnostics -Candidate $candidate -Baseline $baseline
    if (-not $comparison.IsFatal) {
        throw "Identical unparsed failures were incorrectly baseline-subtracted."
    }

    $candidate = New-DiagnosticResult 1 @("[M()] StackUnexpected") @()
    $baseline = New-DiagnosticResult 1 @("[M()] StackUnexpected") @()
    $comparison = Compare-IlverifyDiagnostics -Candidate $candidate -Baseline $baseline
    if ($comparison.IsFatal) {
        throw "A matching parsed compiler diagnostic was not baseline-subtracted."
    }

    $candidate = New-DiagnosticResult 0 @() @()
    $comparison = Compare-IlverifyDiagnostics -Candidate $candidate -Baseline $null
    if ($comparison.IsFatal) {
        throw "A clean verifier success was classified as fatal."
    }

    $candidate = New-DiagnosticResult 1 @("[M()] StackUnexpected", "[N()] ThisMismatch") @()
    $baseline = New-DiagnosticResult 1 @("[M()] StackUnexpected") @()
    $comparison = Compare-IlverifyDiagnostics -Candidate $candidate -Baseline $baseline
    if (-not $comparison.IsFatal -or $comparison.Introduced.Count -ne 1) {
        throw "A newly introduced parsed diagnostic was not classified as fatal."
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

Write-Host "ILVerify diagnostic helper checks passed."
exit 0
