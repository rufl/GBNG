[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$minimumAge = [TimeSpan]::FromMinutes(60)
$cutoffUtc = [DateTime]::UtcNow.Subtract($minimumAge)

$artifactRules = @(
    [pscustomobject]@{
        Name = 'Windows game builds'
        Pattern = 'GameBuilder-Full-Product-*'
        Files = @('built-commit', 'critical-files.txt', 'GameBuilder.exe', 'UnityPlayer.dll')
        Directories = @('GameBuilder_Data')
    },
    [pscustomobject]@{
        Name = 'Linux and macOS game builds'
        Pattern = 'GameBuilder-Monaco-CrossBuild-*'
        Files = @('Linux/GameBuilder.x86_64')
        Directories = @('Linux', 'Mac-arm64/GameBuilder.app', 'Mac-x64/GameBuilder.app')
    },
    [pscustomobject]@{
        Name = 'Direct-host packet smoke builds'
        Pattern = 'DirectHostPacketNetworkSmoke-*'
        Files = @('DirectHostPacketNetworkSmoke.exe', 'UnityPlayer.dll')
        Directories = @('DirectHostPacketNetworkSmoke_Data')
    },
    [pscustomobject]@{
        Name = 'Direct-host chunk smoke builds'
        Pattern = 'DirectHostChunkNetworkSmoke-*'
        Files = @('DirectHostChunkNetworkSmoke.exe', 'UnityPlayer.dll')
        Directories = @('DirectHostChunkNetworkSmoke_Data')
    }
)

$runningProcessPaths = @()
foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
    try {
        if (-not [string]::IsNullOrWhiteSpace($process.Path)) {
            $runningProcessPaths += [System.IO.Path]::GetFullPath($process.Path)
        }
    }
    catch {
        # Process paths may be unavailable for elevated or system processes.
    }
}

$removedDirectories = 0
$removedBytes = [long]0
$keptRecent = 0
$keptActive = 0
$keptUnmarked = 0

foreach ($rule in $artifactRules) {
    $validCandidates = @()
    foreach ($candidate in Get-ChildItem -LiteralPath $tempRoot -Directory -Filter $rule.Pattern -Force -ErrorAction SilentlyContinue) {
        if (($candidate.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $keptUnmarked++
            continue
        }

        $candidatePath = [System.IO.Path]::GetFullPath($candidate.FullName)
        $candidateParent = [System.IO.Path]::GetDirectoryName($candidatePath).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        if (-not [string]::Equals($candidateParent, $tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $keptUnmarked++
            continue
        }

        $hasOwnershipMarkers = $true
        foreach ($relativePath in $rule.Files) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidatePath $relativePath) -PathType Leaf)) {
                $hasOwnershipMarkers = $false
                break
            }
        }
        if ($hasOwnershipMarkers) {
            foreach ($relativePath in $rule.Directories) {
                if (-not (Test-Path -LiteralPath (Join-Path $candidatePath $relativePath) -PathType Container)) {
                    $hasOwnershipMarkers = $false
                    break
                }
            }
        }

        if ($hasOwnershipMarkers) {
            $validCandidates += $candidate
        }
        else {
            $keptUnmarked++
        }
    }

    $validCandidates = @($validCandidates | Sort-Object -Property LastWriteTimeUtc -Descending)
    $newestPath = if ($validCandidates.Count -gt 0) { $validCandidates[0].FullName } else { $null }
    foreach ($candidate in $validCandidates) {
        if ([string]::Equals($candidate.FullName, $newestPath, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($candidate.LastWriteTimeUtc -ge $cutoffUtc) {
            $keptRecent++
            continue
        }

        $candidatePrefix = $candidate.FullName.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        $isRunning = $false
        foreach ($processPath in $runningProcessPaths) {
            if ($processPath.StartsWith($candidatePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $isRunning = $true
                break
            }
        }
        if ($isRunning) {
            $keptActive++
            continue
        }

        try {
            $size = (Get-ChildItem -LiteralPath $candidate.FullName -File -Recurse -Force -ErrorAction SilentlyContinue |
                Measure-Object -Property Length -Sum).Sum
            if ($null -eq $size) {
                $size = 0
            }

            if ($PSCmdlet.ShouldProcess($candidate.FullName, "Remove stale $($rule.Name)")) {
                Remove-Item -LiteralPath $candidate.FullName -Recurse -Force
                $removedDirectories++
                $removedBytes += [long]$size
            }
        }
        catch {
            Write-Warning "Could not remove stale build '$($candidate.FullName)': $($_.Exception.Message)"
        }
    }
}

$reclaimedGiB = [Math]::Round($removedBytes / 1GB, 2)
Write-Output "Temp build cleanup: removed $removedDirectories stale project builds ($reclaimedGiB GiB); kept recent=$keptRecent, active=$keptActive, unmarked/reparse=$keptUnmarked, and the newest build in each family."
