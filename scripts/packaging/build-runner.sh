RELEASE_DIRECTORY=../../../../nethermind-packages
RUNNER_PATH=nethermind/src/Nethermind/Nethermind.Runner
LINUX=linux-x64
OSX=osx-x64
WIN10=win10-x64
PUBLISH_PATH=bin/release/netcoreapp3.1
OUT=out
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

cd $RUNNER_PATH

echo =======================================================
echo Publishing Nethermind Runner for different platforms...
echo =======================================================
echo Nethermind Runner path: $RUNNER_PATH

rm -rf $OUT

dotnet publish -c release -r $LINUX /p:PublishSingleFile=true -o $OUT/$LIN_RELEASE
dotnet publish -c release -r $OSX /p:PublishSingleFile=true -o $OUT/$OSX_RELEASE
dotnet publish -c release -r $WIN10 /p:PublishSingleFile=true -o $OUT/$WIN_RELEASE

rm -rf $OUT/$LIN_RELEASE/Data
rm -rf $OUT/$LIN_RELEASE/Hive
rm $OUT/$LIN_RELEASE/Nethermind.Runner.pdb
rm $OUT/$LIN_RELEASE/web.config
cp -r configs $OUT/$LIN_RELEASE
cp -r ../Chains $OUT/$LIN_RELEASE/chainspec
mkdir $OUT/$LIN_RELEASE/Data
cp Data/static-nodes.json $OUT/$LIN_RELEASE/Data

rm -rf $OUT/$OSX_RELEASE/Data
rm -rf $OUT/$OSX_RELEASE/Hive
rm $OUT/$OSX_RELEASE/Nethermind.Runner.pdb
rm $OUT/$OSX_RELEASE/web.config
cp -r configs $OUT/$OSX_RELEASE
cp -r ../Chains $OUT/$OSX_RELEASE/chainspec
mkdir $OUT/$OSX_RELEASE/Data
cp Data/static-nodes.json $OUT/$OSX_RELEASE/Data

rm -rf $OUT/$WIN_RELEASE/Data
rm -rf $OUT/$WIN_RELEASE/Hive
rm $OUT/$WIN_RELEASE/Nethermind.Runner.pdb
rm $OUT/$WIN_RELEASE/web.config
cp -r configs $OUT/$WIN_RELEASE
cp -r ../Chains $OUT/$WIN_RELEASE/chainspec
mkdir $OUT/$WIN_RELEASE/Data
cp Data/static-nodes.json $OUT/$WIN_RELEASE/Data

mv $OUT/$LIN_RELEASE $RELEASE_DIRECTORY
mv $OUT/$OSX_RELEASE $RELEASE_DIRECTORY
mv $OUT/$WIN_RELEASE $RELEASE_DIRECTORY


rm -rf $OUT

echo =======================================================
echo Building Nethermind Runner completed
echo =======================================================
