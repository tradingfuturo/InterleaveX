# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# Modifications Copyright (c) 2026 pipflow.com <https://pipflow.com>
# Modifications are licensed under the GNU General Public License v3.0 or
# later. See LICENSE-GPL for the full text.

function CheckPSVersion() {
     $ver = $PSVersionTable["PSVersion"]
     if ($ver -lt [version]"7.0.0") {
         Write-Error "Please use latest version of 'pwsh' to run this script"
         Write-Error "You can install it using: dotnet tool install -f powershell" 
         Exit 1
     }
}

# Invokes the specified coyote tool command on the specified target.
function Invoke-CoyoteTool([String]$cmd, [String]$dotnet, [String]$framework, [String]$target, [String]$key, [String]$configuration = "Release") {
    Write-Comment -prefix "..." -text "Rewriting '$target' ($framework)"

    $tool = Join-Path -Path "." -ChildPath "bin" -AdditionalChildPath @($configuration, $framework, "interleavex.exe")
    $command = "$cmd $target"

    if (-not (Test-Path $tool)) {
        $tool = $dotnet
        $coyote = Join-Path -Path "." -ChildPath "bin" -AdditionalChildPath @($configuration, $framework, "interleavex.dll")
        $command = "$coyote $cmd $target"
    }

    if ($command -eq "rewrite" -and $framework -ne "net6.0" -and $framework -ne "net8.0" -and $framework -ne "net9.0" -and $framework -ne "net10.0" -and $IsWindows) {
        # NOTE: Mono.Cecil cannot sign assemblies on unix platforms.
        $command = "$command -snk $key"
    }

    Write-Comment -prefix "..." -text "$tool"
    $error_msg = "Failed to $cmd '$target'"
    Invoke-ToolCommand -tool $tool -cmd $command -error_msg $error_msg
}

# Builds the specified .NET project
function Invoke-DotnetBuild([String]$dotnet, [String]$solution, [String]$config, [bool]$local, [bool]$nuget) {
    Write-Comment -prefix "..." -text "Building $solution"

    $platform = "/p:Platform=`"Any CPU`""
    $restore_command = "restore $solution"
    $build_command = "build -c $config $solution --no-restore"
    if ($local -and $nuget) {
        $nuget_config_file = "$PSScriptRoot/../NuGet.config"
        $restore_command = "$restore_command --configfile $nuget_config_file /p:UseLocalNugetPackages=true $platform"
        $build_command = "$build_command /p:UseLocalNugetPackages=true $platform"
    } elseif ($local) {
        $nuget_config_file = "$PSScriptRoot/../Samples/NuGet.config"
        $restore_command = "$restore_command --configfile $nuget_config_file /p:UseLocalCoyote=true $platform"
        $build_command = "$build_command /p:UseLocalCoyote=true $platform"
    }

    Invoke-ToolCommand -tool $dotnet -cmd $restore_command -error_msg "Failed to restore $solution"
    Invoke-ToolCommand -tool $dotnet -cmd $build_command -error_msg "Failed to build $solution"
}

# Returns the 'dotnet test' command line for the specified target.
#
# Stated once rather than at each caller, because the sequential and sharded paths must run the very
# same command: a flag that reaches only one of them makes the two modes disagree about what they
# measured, which is exactly what nobody would think to check.
function Get-DotnetTestCommand([String]$target, [string]$filter, [string]$framework, [string]$logger, [string]$verbosity, [string]$configuration) {
    $command = "test $target -c $configuration -f $framework --no-build -v $verbosity --logger 'trx' --blame --blame-crash"
    if (!($filter -eq "")) {
        $command = "$command --filter $filter"
    }

    if (!($logger -eq "")) {
        $command = "$command --logger $logger"
    }

    return $command
}

# Runs every framework of one test target and returns what happened, without ending the run.
#
# This is the unit the test script fans out over. Each target is an independent 'dotnet test'
# process, so they have full isolation from one another already -- but only if nothing in here
# reports a failure the way the rest of this module does, by calling 'exit'. See
# 'Invoke-ToolCommandWithResult' for why that would silently pass a failing run.
#
# The 'Stage' says how far the target got, and is what tells a target that was never built apart
# from one whose tests failed. Both used to reach the final 'Done' with exit code 0.
function Invoke-TestTargetShard([hashtable]$Context, [String]$Name, [String]$Project) {
    $output = New-Object System.Text.StringBuilder
    $frameworks_run = @()

    function New-Result([string]$stage, [int]$code) {
        return [pscustomobject]@{
            Target = $Project
            Name = $Name
            Frameworks = $frameworks_run
            Stage = $stage
            ExitCode = $code
            Output = $output.ToString()
        }
    }

    $configuration = $Context.Configuration
    $output_dir = "$($Context.ScriptRoot)/../Tests/$Project/bin/$configuration"
    if (-not (Test-Path $output_dir)) {
        return New-Result "unbuilt" 1
    }

    $target = "$($Context.ScriptRoot)/../Tests/$Project/$Project.csproj"
    $frameworks = Get-ChildItem -Path $output_dir | `
        Where-Object Name -CIn $Context.AllFrameworks | Select-Object -expand Name
    foreach ($f in $frameworks) {
        if ((-not $Context.IsCi) -and ($f -ne $Context.Framework)) {
            continue
        }

        if ($f -eq "net10.0" -or $f -eq "net9.0" -or $f -eq "net8.0") {
            $assembly_name = GetAssemblyName($target)
            $references = @(
                [IO.Path]::Combine($Context.ScriptRoot, "..", "Tests", $Project, "bin", $configuration, $f, "*.dll"),
                [IO.Path]::Combine($Context.ScriptRoot, "..", "bin", $configuration, $f, "*.dll"),
                [IO.Path]::Combine($Context.DotnetRuntimePath, $Context.RuntimeVersion, "*.dll"),
                [IO.Path]::Combine($Context.AspnetRuntimePath, $Context.RuntimeVersion, "*.dll"))

            $rewritten = [IO.Path]::Combine($Context.ScriptRoot, "..", "Tests", $Project, "bin", `
                $configuration, $f, "$assembly_name.dll")

            [void]$output.AppendLine("... Verifying '$Project' ($f)")
            $verified = Invoke-Ilverify -Context $Context -Assembly $rewritten -References $references
            [void]$output.AppendLine($verified.Output)
            if ($verified.ExitCode -ne 0) {
                # What this gate is for is that REWRITING introduced no unverifiable IL, which is not
                # the same thing as the assembly being verifiable. Roslyn emits IL that ECMA-335
                # verification does not cover: a constant ReadOnlySpan<T> hands back a ref struct over
                # RVA data, and ilverify (10.0.0) reports InitOnly, StackUnexpected and ReturnPtrToStack
                # on every one of them (SpanDataRewritingTests exists to exercise exactly that pattern,
                # so it cannot simply stop using it). Those errors are already in the compiler's own
                # output, so they are SUBTRACTED rather than exempted by name: an exemption list goes
                # stale in silence and takes the next member that starts failing for a real reason with
                # it, whereas a baseline that is recomputed every run cannot drift from the compiler.
                # It is also strictly stronger — it covers every member rather than the listed ones.
                $original = [IO.Path]::Combine($Context.ScriptRoot, "..", "Tests", $Project, "obj", `
                    $configuration, $f, "$assembly_name.dll")
                $introduced = $verified.Errors
                if (($verified.Errors.Count -gt 0) -and (Test-Path $original)) {
                    $baseline = Invoke-Ilverify -Context $Context -Assembly $original -References $references
                    $introduced = @($verified.Errors | Where-Object { $baseline.Errors -notcontains $_ })
                    $inherited = $verified.Errors.Count - $introduced.Count
                    [void]$output.AppendLine("... $inherited of $($verified.Errors.Count) error(s) are the " +
                        "compiler's own and are present before rewriting.")
                }

                # An exit code with no error we could parse is the verifier itself failing, and that
                # stays fatal: nothing has been shown about the rewrite either way.
                if ($introduced.Count -gt 0 -or $verified.Errors.Count -eq 0) {
                    [void]$output.AppendLine("Error: found corrupted assembly rewriting in '$Project' ($f).")
                    foreach ($e in $introduced) {
                        [void]$output.AppendLine("  introduced by rewriting: $e")
                    }

                    return New-Result "ilverify" $verified.ExitCode
                }
            }
        }

        [void]$output.AppendLine("... Testing '$Name' ($f)")
        $command = Get-DotnetTestCommand -target $target -filter $Context.Filter -framework $f `
            -logger $Context.Logger -verbosity $Context.Verbosity -configuration $configuration
        $tested = Invoke-ToolCommandWithResult -tool $Context.Dotnet -cmd $command
        [void]$output.AppendLine($tested.Output)
        if ($tested.ExitCode -ne 0) {
            [void]$output.AppendLine("Error: failed to test '$Name' ($f).")
            return New-Result "test" $tested.ExitCode
        }

        $frameworks_run += $f
    }

    if ($frameworks_run.Count -eq 0) {
        return New-Result "empty" 1
    }

    return New-Result "ok" 0
}

# Decides whether a set of shard results is a passing run, and returns the reasons if it is not.
#
# Kept free of any process or file system interaction so that every way a run can fail can be
# checked directly, and so that the sequential and sharded paths reach their verdict through the
# same code rather than through two accountings that agree until they do not.
function Test-ShardOutcome([object[]]$Results, [string[]]$ExpectedTargets) {
    $messages = @()
    $reported = @($Results | Where-Object { $null -ne $_ })

    # A shard that threw, crashed, or was never dispatched produces no result at all. Counting only
    # what came back would read that as a clean run, so absence is failure rather than silence.
    $names = @($reported | ForEach-Object { $_.Target })
    foreach ($target in $ExpectedTargets) {
        if ($names -notcontains $target) {
            $messages += "no result was reported for '$target': its shard did not finish."
        }
    }

    # The stages that say a target never got as far as running anything. These are reported together,
    # naming every target at once, because "no build found for: A, B" is the one line someone needs
    # rather than one line each. Stated as a table so that the reporting below and the exclusion
    # after it cannot disagree about which stages they are: adding a stage here is the whole edit.
    $collected = [ordered]@{ unbuilt = "no build found for"; empty = "no tests ran for" }
    $unrun = $false
    foreach ($stage in $collected.Keys) {
        $targets = @($reported | Where-Object { $_.Stage -eq $stage } | ForEach-Object { $_.Target })
        if ($targets.Count -gt 0) {
            $messages += "$($collected[$stage]): $($targets -join ', ')."
            $unrun = $true
        }
    }

    foreach ($result in ($reported | Where-Object {
            $_.ExitCode -ne 0 -and $collected.Keys -notcontains $_.Stage })) {
        $messages += "'$($result.Target)' failed at the '$($result.Stage)' stage with exit code $($result.ExitCode)."
    }

    # Told apart from an ordinary failure so that the caller can say what to do about it. "Build that
    # configuration first" is the answer to a target that never ran anything, and is noise in front of a
    # test that ran and failed. Derived from the same table as the reporting above rather than from a
    # second list of stage names, for the same reason that table exists.
    #
    # A shard that reported nothing at all deliberately does not count: it crashed or was never
    # dispatched, which is not something a build fixes either.
    return [pscustomobject]@{
        IsFailed = $messages.Count -gt 0
        HasUnrunTargets = $unrun
        Messages = $messages
    }
}

# Runs the specified tool command.
function Invoke-ToolCommand([String]$tool, [String]$cmd, [String]$error_msg) {
    Write-Host "Invoking $tool $cmd"
    Invoke-Expression "$tool $cmd"
    if (-not ($LASTEXITCODE -eq 0)) {
        Write-Error $error_msg
        exit $LASTEXITCODE
    }
}

# Runs the specified tool command and returns its exit code and everything it wrote, rather than
# ending the run.
#
# Every other failure in this file is reported by calling 'exit' in the caller's scope, which works
# only while that scope is the script itself. Inside a parallel runspace 'exit' ends that runspace
# alone: the parent keeps going, sees nothing, and reports success, so a shard whose tests failed
# would pass the run. A caller that fans out therefore has to receive the outcome as a value and
# aggregate it itself.
#
# The output is captured rather than streamed for the same reason. Concurrent shards writing to one
# console interleave into something no one can read, so each shard's output is held and printed as a
# block once it finishes. Both streams are captured, because a tool that fails usually explains
# itself on standard error.
# Verifies one assembly and returns the errors it reported alongside the exit code.
#
# The error lines name the assembly they came from, and the two copies of an assembly this is used to
# compare — the compiler's output in 'obj' and the rewritten one in 'bin' — differ only in that path,
# so it is dropped before the sets are compared. Everything that identifies the error is kept: the
# code, the member, the IL offset and the message.
function Invoke-Ilverify([hashtable]$Context, [String]$Assembly, [String[]]$References) {
    $cmd = '"' + $Assembly + '"'
    foreach ($reference in $References) {
        $cmd = $cmd + ' -r "' + $reference + '"'
    }

    $result = Invoke-ToolCommandWithResult -tool $Context.Ilverify -cmd $cmd
    $errors = @($result.Output -split "`n" |
        Where-Object { $_ -match '^\[IL\]: Error' } |
        ForEach-Object { ($_ -replace '\[[^\]]*\.dll : ', '[').Trim() })

    return [pscustomobject]@{
        ExitCode = $result.ExitCode
        Errors = $errors
        Output = $result.Output
    }
}

function Invoke-ToolCommandWithResult([String]$tool, [String]$cmd) {
    $output = Invoke-Expression "$tool $cmd 2>&1" | Out-String
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

# Returns whether the specified candidate path is inside the specified root directory. The separator
# is appended before comparing, so that a sibling whose name merely starts with the root's name is
# not mistaken for something inside it. A directory is not under itself.
function Test-PathIsUnder([String]$root, [String]$candidate) {
    $separator = [IO.Path]::DirectorySeparatorChar
    $root = [IO.Path]::GetFullPath($root).TrimEnd($separator) + $separator
    $candidate = [IO.Path]::GetFullPath($candidate).TrimEnd($separator) + $separator
    if ($candidate -eq $root) {
        return $false
    }

    return $candidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
}

# Returns the tool and argument prefix that run the benchmark runner out of the specified directory.
# The runner is an 'Exe' project, so its apphost is named 'BenchmarkRunner.exe' on Windows and has no
# extension elsewhere; running the assembly through the dotnet host is the one form that works
# everywhere, and is what the runner's own post-build steps do for the CLI.
function Get-BenchmarkRunnerCommand([String]$directory) {
    $assembly = Join-Path $directory "BenchmarkRunner.dll"
    return @{ Tool = "dotnet"; Prefix = "`"$assembly`""; Assembly = $assembly }
}

# Returns whether a benchmark run actually produced measurements in the specified directory.
#
# The directory itself proves nothing: the runner creates it while parsing its arguments, before a
# single benchmark executes, so it is there after a filter that matched nothing, after a failed
# benchmark build, and after any exception. BenchmarkDotNet writes a report per benchmark into a
# 'results' subdirectory, and that only appears once measurements exist.
function Test-BenchmarksProduced([String]$directory) {
    $results = Join-Path $directory "results"
    if (-not (Test-Path $results)) {
        return $false
    }

    return $null -ne (Get-ChildItem -Path $results -Filter "*-report.csv" -File -ErrorAction SilentlyContinue |
        Select-Object -First 1)
}

# Returns the directory to use for scratch files, on any platform. $ENV:TEMP is unset off Windows,
# where it would yield a relative path inside whatever the working directory happens to be.
function Get-TempDirectory() {
    return [IO.Path]::GetTempPath()
}

function FindProgram([String]$name) {
    $result = $null
    $path = $ENV:PATH.split([System.IO.Path]::PathSeparator) | ForEach-Object {
        $test = Join-Path -Path $_ -ChildPath $name
        if ($IsWindows) {
            $test = $test + ".exe"
        }
        if (Test-Path -Path $test) {
            $result = $test
        }
    }
    return $result
}

function GetAssemblyName([String]$path){
    $AssemblyName = $null;
    $doc = [System.Xml.Linq.XDocument]::Load($path);
    $name = [System.Xml.Linq.XName]::Get("AssemblyName", $r.Name.Namespace);
    $doc.Root.Descendants($name) | ForEach-Object { $AssemblyName = $_.Value };
    return $AssemblyName
}

# Finds the path of the .NET SDK.
function FindDotNetSdkPath([String]$dotnet) {
    $dotnet_sdks = Invoke-Expression "$dotnet --list-sdks"
    $dotnet_sdk_path = $dotnet_sdks | ForEach-Object {
        $sdk_path = ($_ -split {$_ -eq '[' -or $_ -eq ']'})[1]
        return $sdk_path
    }

    if ($dotnet_sdk_path -is [array]) {
        $dotnet_sdk_path = $dotnet_sdk_path[0]
    }

    return $dotnet_sdk_path
}

# Finds the path of the .NET runtime.
function FindDotNetRuntimePath([String]$dotnet, [String]$runtime) {
    $dotnet_runtimes = Invoke-Expression "$dotnet --list-runtimes"
    $dotnet_runtime_path = $dotnet_runtimes | ForEach-Object {
        $runtime_path = ($_ -split {$_ -eq '[' -or $_ -eq ']'})[1]
        if ($runtime_path.Contains($runtime)) {
            return $runtime_path
        }
    }

    if ($dotnet_runtime_path -is [array]) {
        $dotnet_runtime_path = $dotnet_runtime_path[0]
    }

    return $dotnet_runtime_path
}

# Finds the dotnet SDK version.
function FindDotNetSdkVersion([String]$dotnet_sdk_path) {
    $globalJson = Join-Path -Path $PSScriptRoot -ChildPath ".." -AdditionalChildPath @("global.json")
    $json = Get-Content $globalJson | Out-String | ConvertFrom-Json
    $global_version = $json.sdk.version
    Write-Comment -prefix "..." -text "Searching for .NET SDK version '$global_version' in '$dotnet_sdk_path'"
    $matching_version = FindMatchingVersion -path $dotnet_sdk_path -version $global_version
    if ($null -ne $matching_version) {
        if ($global_version -eq $matching_version) {
            Write-Comment -prefix "....." -text "Found expected .NET SDK version '$matching_version'"
        }
    }

    return $matching_version
}

# Finds the dotnet runtime version.
function FindDotNetRuntimeVersion([String]$dotnet_runtime_path) {
    $globalJson = Join-Path -Path $PSScriptRoot -ChildPath ".." -AdditionalChildPath @("global.json")
    $json = Get-Content $globalJson | Out-String | ConvertFrom-Json
    $global_version = $json.sdk.version
    return FindMatchingVersion -path $dotnet_runtime_path -version $global_version
}

# Searches the specified directory for the closest match for the given version.
function FindMatchingVersion([String]$path, [version]$version) {
    $matching_version = $null
    $best_match = $null
    $exact_match = $false
    if ("" -ne $path) {
        foreach ($item in Get-ChildItem $path  -directory) {
            $name = $item.Name
            $global_version = $name
            if ($name.Contains("-preview")) {
                # For the string to be legal in global.json it must
                # be major.minor.patch or major.minor.patch-preview.
                # So we have to remove any preview version like you see
                # in "5.0.100-preview.7.20366.6"
                $name = $name.Split("-preview")[0]
                $global_version = "$name-preview"
            }

            try {
              $v = [version] $name
              if ($v.Major -eq $version.Major -and $v.Minor -eq $version.Minor ) {
                if ($null -eq $best_match) {
                    $best_match = $v
                    $matching_version = $global_version
                }
                elseif ($v.Build -eq $version.Build) {
                    $exact_match = $true
                    $best_match = $v
                    $matching_version = $global_version
                }
                elseif ($v -gt $best_match -and $exact_match -eq $false) {
                    # Use the newest version then.
                    $best_match = $v
                    $matching_version = $global_version
                }
              }
            } catch {
               # Ignore 'NuGetFallbackFolder' and other none version numbered folders.
            }
        }

        return [string] $matching_version
    }
}

function Write-Comment([String]$prefix, [String]$text, [String]$color = "white") {
    if ($prefix.Length -gt 0) {
        $prefix = "$prefix "
    }

    Write-Host $prefix -b "black" -nonewline; Write-Host $text -b "black" -f $color
}

function Write-Error([String]$text) {
    Write-Host "Error: $text" -b "black" -f "red"
}
