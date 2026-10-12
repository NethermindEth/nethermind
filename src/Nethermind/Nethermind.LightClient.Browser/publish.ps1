param(
    [string] $SourceCache = $(if ($IsWindows) { 'C:/Temp/nethermind-lightclient-browser' } else { '/tmp/nethermind-lightclient-browser' })
)

$ErrorActionPreference = 'Stop'
$native = & (Join-Path $PSScriptRoot 'prepare-native.ps1') -OutputDirectory $SourceCache
$precompiles = & (Join-Path $PSScriptRoot 'prepare-precompiles.ps1') -OutputDirectory (Join-Path $SourceCache 'precompiles')
$properties = foreach ($sources in @($native, $precompiles)) {
    foreach ($property in $sources.PSObject.Properties) { "-p:$($property.Name)=$($property.Value)" }
}
& dotnet publish (Join-Path $PSScriptRoot 'Nethermind.LightClient.Browser.csproj') -c release -p:SaveDiskSpace=true -warnaserror @properties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
