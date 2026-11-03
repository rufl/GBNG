[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$projectFile = Join-Path $repoRoot 'project.godot'
if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
    throw "Godot project not found: $projectFile"
}

$version = '1.2.2-stable'
$archiveUrl = "https://github.com/godotengine/webrtc-native/releases/download/$version/godot-extension-webrtc_native.zip"
$expectedSha256 = '98e9446921740d995bd9ca1be48798dc3c2ceed51e044a25ce18b3cff11f56e5'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) "gbng-webrtc-$([guid]::NewGuid().ToString('N'))"
$archive = Join-Path $temporaryRoot 'webrtc-native.zip'
$extractPath = Join-Path $temporaryRoot 'extract'
$target = Join-Path $repoRoot 'addons/webrtc-native'

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archive -UseBasicParsing
    $actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "WebRTC archive SHA256 mismatch. Expected $expectedSha256, received $actualSha256."
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $extractPath -Force
    $manifest = Get-ChildItem -LiteralPath $extractPath -Filter '*.gdextension' -File -Recurse | Select-Object -First 1
    if ($null -eq $manifest) {
        throw 'The verified WebRTC archive contains no .gdextension manifest.'
    }

    if (Test-Path -LiteralPath $target) {
        $marker = Join-Path $target '.gbng-webrtc-version'
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw "Refusing to replace an unmarked directory: $target. Move it yourself, then rerun this installer."
        }
        Remove-Item -LiteralPath $target -Recurse -Force
    }

    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $manifest.Directory.FullName -Force | Copy-Item -Destination $target -Recurse -Force
    Set-Content -LiteralPath (Join-Path $target '.gbng-webrtc-version') -Value $version -Encoding ascii
    Write-Host "Installed godotengine/webrtc-native $version at $target"
    Write-Host 'Restart Godot so it loads the GDExtension. Archive SHA256 was verified.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
