@('test/Cortex.BeaconNode.Tests') | % { 
    dotnet test $_
    if (!$?) { throw 'Tests failed' }
}
$v = (dotnet ./tools/gitversion/GitVersion.dll | ConvertFrom-Json)
dotnet publish src/Cortex.BeaconNode.Host -c Release -p:AssemblyVersion=$($v.AssemblySemVer) -p:FileVersion=$($v.AssemblySemFileVer) -p:Version=$($v.SemVer)+$($v.CommitsSinceVersionSource).Sha.$($v.ShortSha) -p:PackageVersion=$($v.NuGetVersion)

# Recurse does not always work correctly for empty top level directories, so copy the runtimes directory separately
Copy-Item -Path 'src/Cortex.BeaconNode.Host/bin/Release/netcoreapp3.0/publish/*' -Destination "release/$($v.NuGetVersion)" -Force
Copy-Item -Path 'src/Cortex.BeaconNode.Host/bin/Release/netcoreapp3.0/publish/runtimes' -Destination "release/$($v.NuGetVersion)" -Recurse -Force

$hostsettings = Get-Content -Raw -Path "release/$($v.NuGetVersion)/hostsettings.json" | ConvertFrom-Json
$hostsettings.Environment = 'Production'
$hostsettings | ConvertTo-Json | Set-Content -Path "release/$($v.NuGetVersion)/hostsettings.json"
Copy-Item -Path "release/$($v.NuGetVersion)/*" -Destination 'release/latest' -Force
Copy-Item -Path "release/$($v.NuGetVersion)/runtimes" -Destination 'release/latest' -Recurse -Force
