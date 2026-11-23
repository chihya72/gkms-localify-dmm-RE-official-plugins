param([Parameter(Mandatory = $true)][string]$StageRoot)
$ErrorActionPreference = 'Stop'
$StageRoot = (Resolve-Path -LiteralPath $StageRoot).Path
$packages = Join-Path $StageRoot 'plugin-packages'
$runtime = Join-Path $StageRoot 'gakumas-local\re'
$entries = @()
$legacy = @()
$archives = @()
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($folder in Get-ChildItem -LiteralPath $PSScriptRoot -Directory | Sort-Object Name) {
    $manifestFile = Join-Path $folder.FullName 'plugin.json'
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) { continue }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if (-not $ids.Add($manifest.id)) { throw "Duplicate plugin ID: $($manifest.id)" }
    $zipName = [IO.Path]::GetFileNameWithoutExtension($manifest.assembly) + '.zip'
    $zipPath = Join-Path $packages $zipName
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entry = $archive.GetEntry('plugin.json')
        if (-not $entry) { throw "Missing package manifest: $zipName" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $packaged = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($packaged.id -ne $manifest.id -or $packaged.version -ne $manifest.version -or $packaged.apiVersion -ne $manifest.apiVersion -or $packaged.assembly -ne $manifest.assembly) {
            throw "Package does not match source manifest: $zipName"
        }
        if (-not $archive.GetEntry($manifest.assembly)) { throw "Missing plugin assembly: $zipName" }
    } finally { $archive.Dispose() }
    $restart = [bool]$manifest.requiresRestart
    $hotEnable = if ($null -ne $manifest.canHotEnable) { [bool]$manifest.canHotEnable } else { -not $restart }
    $hotDisable = if ($null -ne $manifest.canHotDisable) { [bool]$manifest.canHotDisable } else { -not $restart }
    $settings = ($null -ne $manifest.settings -and @($manifest.settings).Count -gt 0) -or -not [string]::IsNullOrWhiteSpace($manifest.settingsUi)
    $resources = -not [string]::IsNullOrWhiteSpace($manifest.resourceUpdater)
    $entries += [ordered]@{
        InternalName = $manifest.id; Name = $manifest.name; AssemblyVersion = $manifest.version
        GkmsApiLevel = $manifest.apiVersion; Author = $manifest.author; Description = $manifest.description
        DownloadLinkInstall = $zipName; DownloadLinkUpdate = $zipName; Assembly = $manifest.assembly
        Punchline = $manifest.punchline; Category = $manifest.category; Tags = @($manifest.tags)
        RepoUrl = $manifest.repositoryUrl; Names = $manifest.names
        RequiresRestart = -not ($hotEnable -and $hotDisable); HasSettingsUi = $settings; HasResourceUpdater = $resources
    }
    $legacy += [ordered]@{
        id = $manifest.id; name = $manifest.name; version = $manifest.version; apiVersion = $manifest.apiVersion
        author = $manifest.author; description = $manifest.description; downloadUrl = $zipName; assembly = $manifest.assembly
        punchline = $manifest.punchline; category = $manifest.category; tags = @($manifest.tags)
        repositoryUrl = $manifest.repositoryUrl; names = $manifest.names
        requiresRestart = -not ($hotEnable -and $hotDisable); hasSettingsUi = $settings; hasResourceUpdater = $resources
    }
    $archives += $zipPath
}
if ($entries.Count -eq 0) { throw 'No plugin packages were found.' }
$index = [ordered]@{ schemaVersion = 1; name = 'Gakumas Localify RE 官方插件'; plugins = @($legacy) }
$indexPath = Join-Path $packages 'index.json'
$masterPath = Join-Path $packages 'pluginmaster.json'
$index | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $indexPath -Encoding utf8
ConvertTo-Json -InputObject @($entries) -Depth 30 | Set-Content -LiteralPath $masterPath -Encoding utf8
$bundlePath = Join-Path $runtime 'official-plugins.zip'
$temporary = $bundlePath + '.tmp'
$output = [IO.File]::Create($temporary)
try {
    $bundle = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in @($indexPath, $masterPath) + $archives) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($bundle, $file, [IO.Path]::GetFileName($file))
        }
    } finally { $bundle.Dispose() }
} finally { $output.Dispose() }
Move-Item -LiteralPath $temporary -Destination $bundlePath -Force
Write-Output "Published $($entries.Count) official plugins: $masterPath"
Write-Output "Offline bundle: $bundlePath"
