$GodotArguments = $args

$ErrorActionPreference = "Stop"
$cleanupScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'maintenance/cleanup-temp-builds.ps1'
& $cleanupScript

$launcher = Get-Command godot -CommandType Application -ErrorAction Stop
$launcherItem = Get-Item -LiteralPath $launcher.Source
$godotExecutable = $launcher.Source
if ($launcherItem.Target) {
    $target = @($launcherItem.Target)[0]
    if ([System.IO.Path]::IsPathRooted($target)) {
        $godotExecutable = $target
    } else {
        $godotExecutable = Join-Path -Path (Split-Path -Parent $launcher.Source) -ChildPath $target
    }
}

if (-not (Test-Path -LiteralPath $godotExecutable -PathType Leaf)) {
    throw "Godot executable not found: $godotExecutable"
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new($godotExecutable)
$startInfo.WorkingDirectory = (Get-Location).Path
$startInfo.UseShellExecute = $false
foreach ($argument in $GodotArguments) {
    $startInfo.ArgumentList.Add([string]$argument)
}

$process = [System.Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) {
    throw "Could not start Godot: $godotExecutable"
}
$process.WaitForExit()
exit $process.ExitCode
