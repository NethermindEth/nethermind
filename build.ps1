@('test/Cortex.BeaconNode.Tests') | % { 
    dotnet test $_
    if (!$?) { throw 'Tests failed' }
}
$v = (dotnet ./tools/gitversion/GitVersion.dll | ConvertFrom-Json)
dotnet publish src/Cortex.BeaconNode.Host -c Release -p:AssemblyVersion=$($v.AssemblySemVer) -p:FileVersion=$($v.AssemblySemFileVer) -p:Version=$($v.SemVer)+$($v.CommitsSinceVersionSource).Sha.$($v.ShortSha) -p:PackageVersion=$($v.NuGetVersion)
