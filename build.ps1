@('test/Cortex.SimpleSerialize.Tests', 'test/Cortex.BeaconNode.Tests') | % { 
    dotnet test $_ --verbosity normal
    if (!$?) { throw 'Tests failed' }
}
$v = (dotnet ./tools/gitversion/GitVersion.dll | ConvertFrom-Json)
dotnet publish src/Cortex.BeaconNode.Host -c Release -p:InformationalVersion=$($v.SemVer)+$($v.ShortSha) -p:Version=$($v.SemVer)
