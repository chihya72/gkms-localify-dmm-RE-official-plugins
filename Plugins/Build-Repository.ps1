param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$StageRoot,
    [Parameter(Mandatory = $true)][string]$SdkFeed,
    [string]$ReleaseTag
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $StageRoot) { $StageRoot = Join-Path $repository "out\$Configuration" }
$StageRoot = [System.IO.Path]::GetFullPath($StageRoot)
$SdkFeed = (Resolve-Path -LiteralPath $SdkFeed).Path
[string[]]$arguments = @('build', (Join-Path $repository 'Gkms.OfficialPlugins.sln'), '-c', $Configuration, '--nologo', '-v', 'minimal', "-p:StageRoot=$StageRoot", "-p:SdkFeed=$SdkFeed")
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
$parameters = @{ StageRoot = $StageRoot; ReleaseTag = $ReleaseTag }
& (Join-Path $PSScriptRoot 'Export-Repository.ps1') @parameters
