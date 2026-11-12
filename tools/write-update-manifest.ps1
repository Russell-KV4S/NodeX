param([Parameter(Mandatory)][string]$AssetDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$version = $props.SelectSingleNode('//NodeXVersion').InnerText
$code = [long]$props.SelectSingleNode('//NodeXVersionCode').InnerText
$apk = Get-Item -LiteralPath (Join-Path $AssetDirectory 'NodeX-android.apk')
$manifest = [ordered]@{
    schemaVersion = 1
    version = $version
    android = [ordered]@{
        packageId = 'com.kv4s.nodex'
        versionCode = $code
        assetName = $apk.Name
        size = $apk.Length
        sha256 = (Get-FileHash -LiteralPath $apk.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $AssetDirectory 'nodex-update.json') -Encoding utf8NoBOM
$lines = Get-ChildItem -LiteralPath $AssetDirectory -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$lines | Set-Content -LiteralPath (Join-Path $AssetDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM
