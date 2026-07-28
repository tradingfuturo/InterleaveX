# Copyright (c) 2026 pipflow.com <https://pipflow.com>
#
# This file is part of InterleaveX and is licensed under the GNU General
# Public License v3.0 or later. See LICENSE-GPL for the full text.

<#
.SYNOPSIS
Fails when a file references the product build output without naming a configuration.

.DESCRIPTION
Product projects emit into 'bin/<Configuration>/<TFM>' and packages into
'bin/<Configuration>/nuget'. The samples deliberately keep the unqualified 'Samples/bin/<TFM>'
shape, so a reference cannot be judged by its text alone: '..\..\bin\' means 'Samples/bin' in
Samples/CloudMessaging/Raft/Raft.csproj and the product output in Samples/CloudMessaging/run-mock.cmd.

A reference that already names a configuration is accepted wherever it points. Only the rest are
resolved in the context of the file that contains them, and those landing anywhere in the repository
outside Samples are reported.

Documentation is checked separately: a path in a markdown file or an XML-doc example is read with
the working directory at the repository root, so it cannot be resolved lexically.
#>

param(
    # Prints every file that was scanned, rather than only the failures.
    [switch]$verbose
)

$ErrorActionPreference = "Stop"
$RootDir = (Resolve-Path "$PSScriptRoot/..").Path
$SamplesDir = [IO.Path]::GetFullPath((Join-Path $RootDir "Samples"))

# Lines that name the pre-'Separate debug and release build output' layout on purpose, in order to
# explain why it is wrong. Matched on trimmed line content rather than line number so that an edit
# elsewhere in the file does not silently retire the exception.
$Allowed = @(
    @{
        File = "History.md"
        Text = 'configuration-specific directory, so `bin/net8.0` becomes'
        Why  = "changelog entry describing this very change"
    },
    @{
        File = "History.md"
        Text = '`bin/Release/net8.0` and `Tests/X/bin/net8.0` becomes'
        Why  = "changelog entry describing this very change"
    },
    @{
        File = "History.md"
        Text = '`local` feed at `bin/nuget`, and package source mapping routes `InterleaveX*`'
        Why  = "changelog entry naming the feed path it corrects"
    },
    @{
        File = "docs/concepts/binary-rewriting.md"
        Text = '"AssembliesPath": "bin/net8.0",'
        Why  = "the ambiguous example the following section tells you to replace"
    },
    @{
        File = "docs/concepts/binary-rewriting.md"
        Text = '"OutputPath": "bin/net8.0/rewritten",'
        Why  = "the ambiguous example the following section tells you to replace"
    },
    @{
        File = "docs/concepts/binary-rewriting.md"
        Text = 'the assembly that invokes the rewriter. Prefer them over a hard-coded path such as `bin/net8.0`'
        Why  = "prose naming the shape it advises against"
    },
    @{
        File = "Source/Test/Rewriting/RewritingOptions.cs"
        Text = "// 'bin/net8.0' is ambiguous when a project emits both configurations."
        Why  = "comment explaining why the token expansion exists"
    },
    @{
        File = "docs/tutorials/testing-aspnet-service.md"
        Text = "dotnet test bin/net8.0/ImageGalleryTests.dll"
        Why  = "the preceding line changes directory into the sample, so this is sample output"
    },
    @{
        File = "Scripts/run-tests.ps1"
        Text = '$temp_path = "bin/temp"'
        Why  = "scratch directory for the temporary CLI tool install, not build output"
    }
)

# Generated Visual Studio design metadata records absolute developer paths ('C:\git\coyote\bin\net8.0')
# that describe someone else's machine and are rewritten wholesale by the designer.
$SkipExtensions = @(".dgml")
$SkipDirectories = @("bin", "obj", "packages", "Scripts/Notebooks")

# This file quotes the shape it looks for -- in the exceptions below, and in the message it prints
# when it finds one -- so scanning itself would report its own contents. Its own paths are data, not
# references to the build output.
$SkipFiles = @("Scripts/check-build-layout.ps1")

# Resolved lexically against the file that contains the reference.
$PathScanned = @(".csproj", ".props", ".targets", ".ps1", ".psm1", ".cmd", ".bat", ".json", ".config", ".sln", ".yml", ".yaml")

# Read with the working directory at the repository root.
$DocScanned = @(".md", ".cs")

# A reference naming any of these is qualified, wherever it happens to point. Scripts thread the
# configuration through a parameter, so the variable forms count just as the literals do.
$QualifiedSegments = @('$(Configuration)', "Debug", "Release", '$configuration', '$(configuration)')

$findings = New-Object System.Collections.ArrayList
$scanned = 0

function Add-Finding($file, $lineNumber, $text, $reason) {
    [void]$findings.Add([PSCustomObject]@{
        File = $file
        Line = $lineNumber
        Text = $text.Trim()
        Reason = $reason
    })
}

function Test-Allowed($file, $line) {
    $trimmed = $line.Trim()
    foreach ($entry in $Allowed) {
        if (($entry.File -eq $file) -and ($entry.Text -eq $trimmed)) {
            return $true
        }
    }

    return $false
}

# Pulls the whole path token surrounding a 'bin' segment out of a line of script or project markup.
# Stops at the delimiters that end a path in every format scanned here: whitespace, quotes, and the
# XML/command punctuation that surrounds one.
function Get-PathTokens($line) {
    $tokens = New-Object System.Collections.ArrayList
    $pattern = '[A-Za-z0-9_.:$()%~\\/\-]*[\\/]?bin[\\/][A-Za-z0-9_.:$()%~\\/\-]*'
    foreach ($match in [regex]::Matches($line, $pattern)) {
        [void]$tokens.Add($match.Value)
    }

    return $tokens
}

# Resolves the directory a path token is relative to, or $null when the token starts with something
# this script cannot interpret. Returning $null is reported rather than passed: an unrecognised
# prefix is exactly where a stale path would hide.
function Resolve-Anchor($segments, $fileDirectory, $isPipelineFile) {
    if ($isPipelineFile) {
        # A pipeline step resolves its paths against the checkout root, not the YAML file.
        return $RootDir
    }

    if ($segments.Count -eq 0) {
        return $fileDirectory
    }

    $first = $segments[0]
    if (($first -eq '$PSScriptRoot') -or ($first -eq '%~dp0') -or ($first -eq '$(MSBuildThisFileDirectory)')) {
        return $fileDirectory
    }

    if ($first -eq '$RootDir') {
        return $RootDir
    }

    return $fileDirectory
}

function Test-PathReference($file, $lineNumber, $line, $fileDirectory, $isPipelineFile) {
    foreach ($token in (Get-PathTokens $line)) {
        $normalized = $token -replace '\\', '/'
        $segments = @($normalized -split '/' | Where-Object { $_ -ne "" })
        $binIndex = -1
        for ($idx = 0; $idx -lt $segments.Count; $idx++) {
            if ($segments[$idx] -eq "bin") {
                $binIndex = $idx
                break
            }
        }

        if ($binIndex -lt 0) {
            continue
        }

        # Naming a configuration is the whole point, so a reference that does is accepted without
        # working out where it points. Only the rest need resolving, which is what keeps a path
        # built out of unrelated variables from being reported.
        $next = ""
        if ($binIndex + 1 -lt $segments.Count) {
            $next = $segments[$binIndex + 1]
        }

        if ($QualifiedSegments -contains $next) {
            continue
        }

        $prefix = @()
        if ($binIndex -gt 0) {
            $prefix = @($segments[0..($binIndex - 1)])
        }

        # An absolute path names a machine, not this repository; the only ones in tree are in the
        # generated files already skipped by extension.
        if ($normalized -match '^([A-Za-z]:|/)') {
            continue
        }

        $anchor = Resolve-Anchor $prefix $fileDirectory $isPipelineFile
        $unresolved = $false
        $current = $anchor
        $start = 0
        if ($prefix.Count -gt 0) {
            $first = $prefix[0]
            if (($first -eq '$PSScriptRoot') -or ($first -eq '%~dp0') -or ($first -eq '$RootDir') -or
                ($first -eq '$(MSBuildThisFileDirectory)')) {
                $start = 1
            }
        }

        for ($idx = $start; $idx -lt $prefix.Count; $idx++) {
            $segment = $prefix[$idx]
            if ($segment -eq ".") {
                continue
            }
            elseif ($segment -eq "..") {
                $parent = Split-Path $current -Parent
                if ([string]::IsNullOrEmpty($parent)) {
                    $unresolved = $true
                    break
                }

                $current = $parent
            }
            elseif ($segment -match '[$%]') {
                $unresolved = $true
                break
            }
            else {
                $current = Join-Path $current $segment
            }
        }

        if ($unresolved) {
            Add-Finding $file $lineNumber $line "path prefix '$token' could not be resolved; review it by hand"
            continue
        }

        $resolved = [IO.Path]::GetFullPath((Join-Path $current "bin"))

        # The samples keep the unqualified shape on purpose, so their own output is not our business.
        if (($resolved -eq $SamplesDir) -or ($resolved.StartsWith($SamplesDir + [IO.Path]::DirectorySeparatorChar))) {
            continue
        }

        if (-not $resolved.StartsWith($RootDir + [IO.Path]::DirectorySeparatorChar)) {
            continue
        }

        Add-Finding $file $lineNumber $line "'$token' resolves to product output that does not name a configuration"
    }
}

function Test-DocReference($file, $lineNumber, $line) {
    $pattern = '(?<!Samples[\\/])bin[\\/](net[0-9]|netstandard|nuget)'
    if ($line -match $pattern) {
        Add-Finding $file $lineNumber $line "documented path refers to the product output without a configuration"
    }
}

Push-Location $RootDir
try {
    $tracked = & git ls-files
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Unable to list the tracked files; this script must run inside the repository." -ForegroundColor red
        exit 1
    }
}
finally {
    Pop-Location
}

foreach ($file in $tracked) {
    $extension = [IO.Path]::GetExtension($file)
    if ($SkipExtensions -contains $extension) {
        continue
    }

    if ($SkipFiles -contains $file) {
        continue
    }

    $skip = $false
    foreach ($directory in $SkipDirectories) {
        if (($file -like "$directory/*") -or ($file -like "*/$directory/*")) {
            $skip = $true
            break
        }
    }

    if ($skip) {
        continue
    }

    $isDoc = $DocScanned -contains $extension
    $isPath = $PathScanned -contains $extension
    if ((-not $isDoc) -and (-not $isPath)) {
        continue
    }

    $full = Join-Path $RootDir $file
    if (-not (Test-Path $full)) {
        continue
    }

    $scanned++
    if ($verbose) {
        Write-Host "... scanning $file"
    }

    $fileDirectory = Split-Path $full -Parent
    $isPipelineFile = $file -like "Scripts/CI/*"
    $lineNumber = 0
    foreach ($line in (Get-Content $full)) {
        $lineNumber++
        if ($line -notmatch 'bin[\\/]') {
            continue
        }

        if (Test-Allowed $file $line) {
            continue
        }

        if ($isDoc) {
            Test-DocReference $file $lineNumber $line
        }
        else {
            Test-PathReference $file $lineNumber $line $fileDirectory $isPipelineFile
        }
    }
}

if ($findings.Count -eq 0) {
    Write-Host ". Checked $scanned files; the build output layout is consistent." -ForegroundColor green
    exit 0
}

Write-Host ". Checked $scanned files and found $($findings.Count) stale reference(s) to the product build output." -ForegroundColor red
Write-Host "  Product output is 'bin/<Configuration>/<TFM>' and packages are 'bin/<Configuration>/nuget'." -ForegroundColor red
foreach ($finding in ($findings | Sort-Object File, Line)) {
    Write-Host ""
    Write-Host "  $($finding.File):$($finding.Line)" -ForegroundColor yellow
    Write-Host "    $($finding.Text)"
    Write-Host "    $($finding.Reason)"
}

Write-Host ""
exit 1
