@('../Nethermind.BeaconNode.Test') | % { 
    dotnet test $_
    if (!$?) { throw 'Tests failed' }
}

if (Test-Path 'bin/Release') {
    Remove-Item -Path 'bin/Release' -Recurse
}

$v = (dotnet ./tools/gitversion/GitVersion.dll | ConvertFrom-Json)
dotnet publish ./Nethermind.BeaconNode.Host.csproj -c Release -p:AssemblyVersion=$($v.AssemblySemVer) -p:FileVersion=$($v.AssemblySemFileVer) -p:Version=$($v.SemVer)+$($v.ShortSha) -p:PackageVersion=$($v.NuGetVersion)

$versionFolder = "release/$($v.NuGetVersion)"
if (Test-Path $versionFolder) {
    Remove-Item -Path $versionFolder -Recurse
}
New-Item -Path $versionFolder -ItemType Directory
Copy-Item -Path './bin/Release/netcoreapp3.0/publish/*' -Destination $versionFolder -Recurse

$hostsettings = Get-Content -Raw -Path "$($versionFolder)/hostsettings.json" | ConvertFrom-Json
$hostsettings.Environment = 'Production'
$hostsettings | ConvertTo-Json | Set-Content -Path "$($versionFolder)/hostsettings.json"

$latestFolder = 'release/latest'
if (Test-Path $latestFolder) {
    Remove-Item -Path $latestFolder -Recurse
}
New-Item -Path $latestFolder -ItemType Directory
Copy-Item -Path "$($versionFolder)/*" -Destination $latestFolder -Recurse
