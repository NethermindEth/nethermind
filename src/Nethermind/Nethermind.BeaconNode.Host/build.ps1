Write-Host 'Building Nethermind.BeaconNode.Host'

@(Join-Path $PSScriptRoot '../Nethermind.BeaconNode.Test') | % { 
    dotnet test $_
    if (!$?) { throw 'Tests failed' }
}

if (Test-Path (Join-Path $PSScriptRoot '/bin/Release')) {
    Remove-Item -Path (Join-Path $PSScriptRoot 'bin/Release') -Recurse
}

dotnet publish (Join-Path $PSScriptRoot 'Nethermind.BeaconNode.Host.csproj') -c Release

$latestFolder = Join-Path $PSScriptRoot 'release/latest'
if (Test-Path $latestFolder) {
    Remove-Item -Path $latestFolder -Recurse
}
New-Item -Path $latestFolder -ItemType Directory -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'bin/Release/netcoreapp3.0/publish/*') -Destination $latestFolder -Recurse

$hostsettings = Get-Content -Raw -Path (Join-Path $latestFolder 'hostsettings.json') | ConvertFrom-Json
$hostsettings.Environment = 'Production'
$hostsettings | ConvertTo-Json | Set-Content -Path (Join-Path $latestFolder 'hostsettings.json')

#$gitversion = Join-Path $PSScriptRoot 'tools/gitversion/GitVersion.dll' 
#$v = (dotnet $gitversion | ConvertFrom-Json)
#$versionFolder = Join-Path $PSScriptRoot "release/$($v.NuGetVersion)"
#if (Test-Path $versionFolder) {
#    Remove-Item -Path $versionFolder -Recurse
#}
#New-Item -Path $versionFolder -ItemType Directory -Force
#Copy-Item -Path (Join-Path $latestFolder '*') -Destination $versionFolder -Recurse

