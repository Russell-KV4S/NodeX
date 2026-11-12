param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$staging = Join-Path ([IO.Path]::GetTempPath()) ('nodex-package-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $staging 'publish'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
dotnet publish (Join-Path $root 'src/NodeX.UI/NodeX.UI.Desktop/NodeX.UI.Desktop.csproj') -c Release -r $Runtime --self-contained true -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $publish
}
if ($Runtime.StartsWith('osx-')) {
    if (-not $IsMacOS) { throw 'Package macOS releases on a Mac to retain executable permissions.' }
    $bundle = Join-Path $staging 'NodeX.app'
    $macos = Join-Path $bundle 'Contents/MacOS'
    New-Item -ItemType Directory -Path $macos -Force | Out-Null
    Copy-Item -Path (Join-Path $publish '*') -Destination $macos -Recurse
    [xml]$plist = Get-Content -LiteralPath (Join-Path $root 'src/NodeX.UI/NodeX.UI.Desktop/macos/Info.plist') -Raw
    [xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    $plist.SelectSingleNode('//key[text()="CFBundleShortVersionString"]').NextSibling.InnerText = $props.SelectSingleNode('//NodeXVersion').InnerText
    $plist.SelectSingleNode('//key[text()="CFBundleVersion"]').NextSibling.InnerText = $props.SelectSingleNode('//NodeXVersionCode').InnerText
    $plist.Save((Join-Path $bundle 'Contents/Info.plist'))
    & chmod +x (Join-Path $macos 'NodeX.UI.Desktop')
    if ($LASTEXITCODE -ne 0) { throw 'Could not set macOS executable permissions.' }
    # Ad-hoc signing makes the bundle internally consistent; this is not Apple signing/notarization.
    & codesign --force --deep --sign - $bundle
    if ($LASTEXITCODE -ne 0) { throw 'macOS ad-hoc signing failed.' }
    & ditto -c -k --sequesterRsrc --keepParent $bundle (Join-Path $output "NodeX-$Runtime.zip")
} elseif ($Runtime.StartsWith('linux-')) {
    if (-not $IsLinux) { throw 'Package Linux releases on Linux to retain executable permissions.' }
    & chmod +x (Join-Path $publish 'NodeX.UI.Desktop')
    if ($LASTEXITCODE -ne 0) { throw 'Could not set Linux executable permissions.' }
    & tar -czf (Join-Path $output "NodeX-$Runtime.tar.gz") -C $publish .
} else {
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $output "NodeX-$Runtime.zip") -Force
}
if ($LASTEXITCODE -ne 0) { throw 'Release archive creation failed.' }
Write-Output "Packaged $Runtime in $output"
