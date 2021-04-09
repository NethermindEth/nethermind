#!/bin/bash
#exit when any command fails
set -e
RUNNER_PATH=$RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Runner
ARM_ROCKSDB_PATH=$RELEASE_DIRECTORY/nethermind/scripts/deployment/arm64/runtimes
PUBLISH_PATH=bin/release/net5.0
OUT=out

cd $RUNNER_PATH

echo =======================================================
echo Publishing Nethermind Runner for different platforms...
echo =======================================================
echo Nethermind Runner path: $RUNNER_PATH

dotnet publish -c release -r $LINUX -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$LIN_RELEASE
dotnet publish -c release -r $OSX -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$OSX_RELEASE
dotnet publish -c release -r $WIN10 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$WIN_RELEASE

cp $ARM_ROCKSDB_PATH/librocksdb.so ../../rocksdb-sharp/RocksDbNative/runtimes/linux-arm64/native/librocksdb.so
dotnet publish -c release -r $LINUX_ARM64 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$LIN_ARM64_RELEASE

rm -rf $OUT/$LIN_RELEASE/Data
rm -rf $OUT/$LIN_RELEASE/Hive
rm $OUT/$LIN_RELEASE/*.pdb
cp -r configs $OUT/$LIN_RELEASE
cp -r ../Chains $OUT/$LIN_RELEASE/chainspec
mkdir $OUT/$LIN_RELEASE/Data
mkdir $OUT/$LIN_RELEASE/keystore
cp Data/static-nodes.json $OUT/$LIN_RELEASE/Data

rm -rf $OUT/$OSX_RELEASE/Data
rm -rf $OUT/$OSX_RELEASE/Hive
rm $OUT/$OSX_RELEASE/*.pdb
cp -r configs $OUT/$OSX_RELEASE
cp -r ../Chains $OUT/$OSX_RELEASE/chainspec
mkdir $OUT/$OSX_RELEASE/Data
mkdir $OUT/$OSX_RELEASE/keystore
cp Data/static-nodes.json $OUT/$OSX_RELEASE/Data

rm -rf $OUT/$WIN_RELEASE/Data
rm -rf $OUT/$WIN_RELEASE/Hive
rm $OUT/$WIN_RELEASE/*.pdb
cp -r configs $OUT/$WIN_RELEASE
cp -r ../Chains $OUT/$WIN_RELEASE/chainspec
mkdir $OUT/$WIN_RELEASE/Data
mkdir $OUT/$WIN_RELEASE/keystore
cp Data/static-nodes.json $OUT/$WIN_RELEASE/Data

rm -rf $OUT/$LIN_ARM64_RELEASE/Data
rm -rf $OUT/$LIN_ARM64_RELEASE/Hive
rm $OUT/$LIN_ARM64_RELEASE/*.pdb
cp -r configs $OUT/$LIN_ARM64_RELEASE
cp -r ../Chains $OUT/$LIN_ARM64_RELEASE/chainspec
mkdir $OUT/$LIN_ARM64_RELEASE/Data
mkdir $OUT/$LIN_ARM64_RELEASE/keystore
cp Data/static-nodes.json $OUT/$LIN_ARM64_RELEASE/Data

mv $OUT/$LIN_RELEASE $RELEASE_DIRECTORY
mv $OUT/$OSX_RELEASE $RELEASE_DIRECTORY
mv $OUT/$WIN_RELEASE $RELEASE_DIRECTORY
mv $OUT/$LIN_ARM64_RELEASE $RELEASE_DIRECTORY

rm -rf $OUT

echo =======================================================
echo Building Nethermind Runner completed
echo =======================================================