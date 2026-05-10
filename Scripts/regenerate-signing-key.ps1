# Regenerate the InterleaveX strong-name signing key.
#
# This script generates a new .snk for the InterleaveX fork and updates every
# `InternalsVisibleTo` PublicKey token in the source tree to match the new
# key.
#
# IMPORTANT — run this once when establishing the fork, then commit the
# results. The committed `coyote/Common/Key.snk` and the regenerated
# `InternalsVisibleTo.cs` files together pin the fork's signing identity.
# DO NOT re-run this script on every build — it is destructive.
#
# Prerequisites:
# - `sn.exe` from a Visual Studio Developer Command Prompt (Windows), OR
# - The .NET SDK with the `dotnet sn` equivalent.
#
# Usage (from the repo root):
#   pwsh -f Scripts/regenerate-signing-key.ps1
#
# After running, build and verify the public key token of the produced
# assemblies matches what is in InternalsVisibleTo.cs. Then commit the .snk
# and the InternalsVisibleTo.cs edits in a single commit titled e.g.
# "Establish InterleaveX strong-name signing key".

[CmdletBinding()]
param(
    [string]$KeyPath = (Join-Path $PSScriptRoot "../Common/Key.snk")
)

$ErrorActionPreference = 'Stop'

Write-Host "Regenerating InterleaveX strong-name key at $KeyPath" -ForegroundColor Cyan

# 1. Locate sn.exe.
$sn = (Get-Command sn.exe -ErrorAction SilentlyContinue)
if ($null -eq $sn) {
    throw "sn.exe not found on PATH. Run this from a Visual Studio Developer Command Prompt, or install the Windows SDK."
}

# 2. Back up the existing key (just in case).
if (Test-Path $KeyPath) {
    $backup = "$KeyPath.bak"
    Copy-Item $KeyPath $backup -Force
    Write-Host "Backed up existing key to $backup" -ForegroundColor Yellow
}

# 3. Generate the new key.
& sn.exe -k $KeyPath
Write-Host "Generated new key at $KeyPath" -ForegroundColor Green

# 4. Extract the full public key (the long hex blob embedded in PublicKey=...
#    in InternalsVisibleTo attributes).
$pubFile = "$KeyPath.pub"
& sn.exe -p $KeyPath $pubFile

# Format the public key as a continuous hex string (uppercase or lowercase —
# the format used in the existing InternalsVisibleTo.cs is lowercase).
$pubBytes = [System.IO.File]::ReadAllBytes($pubFile)
$pubHex = ($pubBytes | ForEach-Object { $_.ToString('x2') }) -join ''
Remove-Item $pubFile

Write-Host ""
Write-Host "New public key (lowercase hex):" -ForegroundColor Cyan
Write-Host $pubHex

# 5. Update every InternalsVisibleTo.cs in the source tree.
$ivtFiles = Get-ChildItem -Path (Join-Path $PSScriptRoot "../Source") -Recurse -Filter "InternalsVisibleTo.cs"
if ($ivtFiles.Count -eq 0) {
    Write-Warning "No InternalsVisibleTo.cs files found under Source/. Skipping update."
}

# The existing files use a multi-line string concatenation pattern:
#   "PublicKey=" +
#   "0024..." +
#   "...67e" (last line ends BEFORE the closing quote-paren-bracket)
# We collapse the entire PublicKey value into a single concatenation expression
# split into 78-char-per-line chunks (matching the upstream style).

function Format-PublicKeyAsCsharpLiteral {
    param([string]$Hex)
    # 78 chars per line is the upstream convention; the last line may be shorter.
    $chunkSize = 78
    $lines = @()
    for ($i = 0; $i -lt $Hex.Length; $i += $chunkSize) {
        $end = [Math]::Min($i + $chunkSize, $Hex.Length)
        $lines += '"' + $Hex.Substring($i, $end - $i) + '"'
    }
    return $lines -join " +`r`n    "
}

$formattedKey = Format-PublicKeyAsCsharpLiteral $pubHex

foreach ($file in $ivtFiles) {
    Write-Host "Updating $($file.FullName)" -ForegroundColor Cyan
    $content = Get-Content $file.FullName -Raw

    # Replace every PublicKey="..."+...+"..." block with the new key.
    # Match: "...,PublicKey=" + (multi-line concatenated hex strings).
    $pattern = '(?s)("[^"]+,PublicKey=" \+\s*\r?\n\s*)("[0-9a-fA-F]{2,}"(?:\s*\+\s*\r?\n\s*"[0-9a-fA-F]{2,}")*)'
    $replacement = '${1}' + $formattedKey
    $newContent = [System.Text.RegularExpressions.Regex]::Replace($content, $pattern, $replacement)

    Set-Content -Path $file.FullName -Value $newContent -Encoding utf8
}

Write-Host ""
Write-Host "Done. Review the diff with 'git diff' before committing." -ForegroundColor Green
Write-Host "Suggested commit: 'Establish InterleaveX strong-name signing key'" -ForegroundColor Green
