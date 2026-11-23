param([Parameter(Mandatory = $true)][string]$StageRoot)
$ErrorActionPreference = 'Stop'
$online = @(Get-Content -LiteralPath (Join-Path $StageRoot 'online\pluginmaster.json') -Raw | ConvertFrom-Json)
$legacy = Get-Content -LiteralPath (Join-Path $StageRoot 'online\index.json') -Raw | ConvertFrom-Json
$bundle = [IO.Compression.ZipFile]::OpenRead((Join-Path $StageRoot 'gakumas-local\re\official-plugins.zip'))
try {
    $reader = [IO.StreamReader]::new($bundle.GetEntry('pluginmaster.json').Open())
    try { $offline = @($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    if ($online.Count -ne 14 -or $offline.Count -ne 14 -or @($legacy.plugins).Count -ne 14 -or $bundle.Entries.Count -ne 16) { throw 'Expected 14 plugin packages and two indexes.' }
    if (@($online.InternalName | Select-Object -Unique).Count -ne 14) { throw 'Duplicate plugin IDs.' }
    foreach ($plugin in $online) {
        $uri = [uri]$plugin.DownloadLinkInstall
        if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.Host -ne 'raw.githubusercontent.com' -or $uri.AbsolutePath -notmatch '^/chihya72/gkms-localify-dmm-RE-official-plugins/[A-Za-z0-9._-]+/packages/[^/]+\.zip$' -or $plugin.DownloadLinkUpdate -ne $uri.AbsoluteUri) { throw "Invalid repository URL: $uri" }
        $local = @($offline | Where-Object InternalName -EQ $plugin.InternalName)
        $old = @($legacy.plugins | Where-Object id -EQ $plugin.InternalName)
        if ($local.Count -ne 1 -or $old.Count -ne 1 -or $local[0].AssemblyVersion -ne $plugin.AssemblyVersion -or $old[0].downloadUrl -ne $plugin.DownloadLinkInstall) { throw 'Catalogs differ.' }
        $name = [IO.Path]::GetFileNameWithoutExtension($plugin.Assembly) + '.zip'
        $publishedName = [IO.Path]::GetFileNameWithoutExtension($plugin.Assembly) + '-' + $plugin.AssemblyVersion + '.zip'
        if ([IO.Path]::GetFileName($uri.AbsolutePath) -ne $publishedName) { throw 'Published package filename must include its version.' }
        if ($local[0].DownloadLinkInstall -ne $name -or -not $bundle.GetEntry($name)) { throw 'Offline links must resolve inside the bundle.' }
        $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path (Join-Path $StageRoot 'plugin-packages') $name))
        try {
            $reader = [IO.StreamReader]::new($zip.GetEntry('plugin.json').Open())
            try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($manifest.id -ne $plugin.InternalName -or $manifest.version -ne $plugin.AssemblyVersion -or $manifest.apiVersion -ne $plugin.GkmsApiLevel -or -not $zip.GetEntry($manifest.assembly)) { throw "Package mismatch: $name" }
        } finally { $zip.Dispose() }
    }
} finally { $bundle.Dispose() }
Write-Output 'PASS: 14 plugin packages, raw Git URLs and self-contained offline catalog.'
