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
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}

Write-Host "ILVerify diagnostic helper checks passed."
exit 0
