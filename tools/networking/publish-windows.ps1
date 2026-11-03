[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$PublishDir = (Join-Path $PSScriptRoot 'publish/win-x64')
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$project = Join-Path $repoRoot 'services/GBNG.Matchmaking/GBNG.Matchmaking.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Matchmaking project not found: $project"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'The .NET SDK is required. Install .NET 10 SDK and ensure dotnet is on PATH.'
}
$sdkList = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdkList | Where-Object { $_ -match '^10\.' })) {
    throw 'A .NET 10 SDK is required to publish this project. Install it and ensure dotnet is on PATH.'
}

if ([System.IO.Path]::IsPathRooted($PublishDir)) {
    $output = [System.IO.Path]::GetFullPath($PublishDir)
} else {
    $output = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $PublishDir))
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true -p:UseAppHost=true --output $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
Write-Host "Published self-contained win-x64 matchmaking service to: $output"
Write-Host 'This command only publishes files; it does not install or start a service.'
