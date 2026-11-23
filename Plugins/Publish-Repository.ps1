param([Parameter(Mandatory = $true)][string]$StageRoot)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$StageRoot = (Resolve-Path -LiteralPath $StageRoot).Path
function Invoke-Git([string[]]$Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($Arguments -join ' ')" }
}
$parameters = @{ StageRoot = $StageRoot }
& (Join-Path $PSScriptRoot 'Test-Repository.ps1') @parameters
if (-not $?) { throw 'Package verification failed.' }
$catalog = Get-Content -LiteralPath (Join-Path $StageRoot 'online\pluginmaster.json') -Raw | ConvertFrom-Json
$destination = Join-Path $repository 'packages'
[void][IO.Directory]::CreateDirectory($destination)
foreach ($plugin in $catalog) {
    $sourceName = [IO.Path]::GetFileNameWithoutExtension($plugin.Assembly) + '.zip'
    $name = [IO.Path]::GetFileNameWithoutExtension($plugin.Assembly) + '-' + $plugin.AssemblyVersion + '.zip'
    Copy-Item -LiteralPath (Join-Path (Join-Path $StageRoot 'plugin-packages') $sourceName) -Destination (Join-Path $destination $name) -Force
}
Invoke-Git -Arguments @('-C', $repository, 'add', '--', 'packages')
$changes = @(Invoke-Git -Arguments @('-C', $repository, 'diff', '--cached', '--name-only'))
if ($changes.Count -gt 0) { Invoke-Git -Arguments @('-C', $repository, 'commit', '-m', 'Publish tested plugin packages') }
$revision = Invoke-Git -Arguments @('-C', $repository, 'rev-parse', 'HEAD')
& (Join-Path $PSScriptRoot 'Export-Repository.ps1') -StageRoot $StageRoot -PackageRevision $revision
if (-not $?) { throw 'Repository export failed.' }
& (Join-Path $PSScriptRoot 'Test-Repository.ps1') @parameters
if (-not $?) { throw 'Published catalog verification failed.' }
foreach ($name in @('pluginmaster.json', 'index.json')) {
    Copy-Item -LiteralPath (Join-Path (Join-Path $StageRoot 'online') $name) -Destination (Join-Path $repository $name) -Force
}
Invoke-Git -Arguments @('-C', $repository, 'add', '--', 'pluginmaster.json', 'index.json')
$changes = @(Invoke-Git -Arguments @('-C', $repository, 'diff', '--cached', '--name-only'))
if ($changes.Count -gt 0) { Invoke-Git -Arguments @('-C', $repository, 'commit', '-m', 'Update official plugin subscription indexes') }
# A normal fast-forward push publishes both commits together and rejects concurrent source changes.
Invoke-Git -Arguments @('-C', $repository, 'push', 'origin', 'HEAD:refs/heads/main')
Write-Output 'Subscription: https://raw.githubusercontent.com/chihya72/gkms-localify-dmm-RE-official-plugins/main/pluginmaster.json'
