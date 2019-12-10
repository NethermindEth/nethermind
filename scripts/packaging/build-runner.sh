RELEASE_DIRECTORY=../../../../nethermind-packages
RUNNER_PATH=nethermind/src/Nethermind/Nethermind.Runner
LINUX=linux-x64
OSX=osx-x64
WIN10=win10-x64
PUBLISH_PATH=bin/release/netcoreapp3.0
EXEC=Nethermind.Runner
ZIP=$EXEC.zip
OUT=out
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

cd $RUNNER_PATH

echo =======================================================
echo Publishing Nethermind Runner for different platforms...
echo =======================================================
echo Nethermind Runner path: $RUNNER_PATH

dotnet tool restore
assemblyversion=$(dotnet gitversion -output json -showvariable AssemblySemVer)
fileversion=$(dotnet gitversion -output json -showvariable AssemblySemFileVer)
semver=$(dotnet gitversion -output json -showvariable SemVer)
shortsha=$(dotnet gitversion -output json -showvariable ShortSha)

dotnet publish -c release -r $LINUX -p:AssemblyVersion=$assemblyversion -p:FileVersion=$fileversion -p:Version=${semver}+${shortsha}
dotnet publish -c release -r $OSX -p:AssemblyVersion=$assemblyversion -p:FileVersion=$fileversion -p:Version=${semver}+${shortsha}
dotnet publish -c release -r $WIN10 -p:AssemblyVersion=$assemblyversion -p:FileVersion=$fileversion -p:Version=${semver}+${shortsha}

rm -rf $OUT && mkdir $OUT $OUT/$LINUX $OUT/$OSX $OUT/$WIN10

echo =======================================================
echo Packing Nethermind Runner for different platforms...
echo =======================================================

/usr/local/bin/warp-packer --arch linux-x64 --input_dir $PUBLISH_PATH/$LINUX/publish --exec $EXEC --output $OUT/$LINUX/$EXEC
/usr/local/bin/warp-packer --arch macos-x64 --input_dir $PUBLISH_PATH/$OSX/publish --exec $EXEC --output $OUT/$OSX/$EXEC
/usr/local/bin/warp-packer --arch windows-x64 --input_dir $PUBLISH_PATH/$WIN10/publish --exec $EXEC.exe --output $OUT/$WIN10/$EXEC.exe

mv $OUT/$LINUX/$EXEC $RELEASE_DIRECTORY/$LIN_RELEASE
mv $OUT/$OSX/$EXEC $RELEASE_DIRECTORY/$OSX_RELEASE
mv $OUT/$WIN10/$EXEC.exe $RELEASE_DIRECTORY/$WIN_RELEASE

cp -r configs $RELEASE_DIRECTORY/$LIN_RELEASE
cp -r configs $RELEASE_DIRECTORY/$OSX_RELEASE
cp -r configs $RELEASE_DIRECTORY/$WIN_RELEASE 

rm -rf $OUT

echo =======================================================
echo Building Nethermind Runner completed
echo =======================================================
