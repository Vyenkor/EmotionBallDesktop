[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.4'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '.release'))
$packageName = "Emotionball-Deskpet-v$Version-win-x64"
$packageDirectory = [IO.Path]::GetFullPath((Join-Path $releaseRoot $packageName))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $releaseRoot ".publish-$Version"))
$archivePath = [IO.Path]::GetFullPath((Join-Path $releaseRoot "$packageName.zip"))
$checksumPath = "$archivePath.sha256"
$setupPublishDirectory = [IO.Path]::GetFullPath((Join-Path $releaseRoot ".setup-publish-$Version"))
$setupPath = [IO.Path]::GetFullPath((Join-Path $releaseRoot "Emotionball-Deskpet-v$Version-setup.exe"))
$setupChecksumPath = "$setupPath.sha256"

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releasePrefix = $releaseRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($packageDirectory, $publishDirectory, $setupPublishDirectory)) {
    if (-not $target.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release target: $target"
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
foreach ($target in @($archivePath, $checksumPath, $setupPath, $setupChecksumPath)) {
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Force
    }
}

dotnet publish (Join-Path $projectRoot 'desktop-host\Emotionball-Deskpet.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:BundlePrivateFonts=false

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $publishDirectory -File |
    Where-Object { $_.Extension -notin @('.pdb', '.xml') } |
    Copy-Item -Destination $packageDirectory

$resourcesDirectory = Join-Path $packageDirectory 'resources'
New-Item -ItemType Directory -Path $resourcesDirectory -Force | Out-Null
foreach ($directoryName in @('bridge', 'js', 'css')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $directoryName) -Destination $resourcesDirectory -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $resourcesDirectory 'assets') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets\img') -Destination (Join-Path $resourcesDirectory 'assets') -Recurse
foreach ($fileName in @('desktop.html', 'codex.html')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $fileName) -Destination $resourcesDirectory
}

$nodeCommand = Get-Command node -ErrorAction Stop
$nodeVersion = (& $nodeCommand.Source --version).Trim().TrimStart('v')
$runtimeDirectory = Join-Path $resourcesDirectory 'runtime'
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $runtimeDirectory 'node.exe')
$nodeLicenseUri = "https://raw.githubusercontent.com/nodejs/node/v$nodeVersion/LICENSE"
Invoke-WebRequest -Uri $nodeLicenseUri -OutFile (Join-Path $runtimeDirectory 'LICENSE') -UseBasicParsing

foreach ($fileName in @('README.md', 'README.en.md', 'LICENSE', 'LICENSE-COMMERCIAL.md', 'NOTICE.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $fileName) -Destination $packageDirectory
}

$versionText = @"
Emotionball-Deskpet $Version
Windows 10/11 x64 portable release
Built: $((Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz'))
Upstream: https://github.com/sam70361/emotion-ball
Project: https://github.com/Vyenkor/Emotionball-Deskpet
License: non-commercial learning and exchange; see LICENSE
"@
[IO.File]::WriteAllText((Join-Path $packageDirectory 'VERSION.txt'), $versionText, [Text.UTF8Encoding]::new($false))

Compress-Archive -LiteralPath $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText($checksumPath, "$hash  $([IO.Path]::GetFileName($archivePath))`n", [Text.UTF8Encoding]::new($false))

dotnet publish (Join-Path $projectRoot 'installer\Emotionball-Deskpet.Setup.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $setupPublishDirectory `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PayloadZip=$archivePath

Copy-Item -LiteralPath (Join-Path $setupPublishDirectory 'Emotionball-Deskpet-Setup.exe') -Destination $setupPath
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText($setupChecksumPath, "$setupHash  $([IO.Path]::GetFileName($setupPath))`n", [Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $publishDirectory -Recurse -Force
Remove-Item -LiteralPath $setupPublishDirectory -Recurse -Force

[pscustomobject]@{
    Package = $packageDirectory
    Archive = $archivePath
    Sha256 = $hash
    SizeMB = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 2)
    Setup = $setupPath
    SetupSha256 = $setupHash
    SetupSizeMB = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
}
