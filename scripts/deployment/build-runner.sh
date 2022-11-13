#!/bin/bash
#exit when any command fails
set -e

RUNNER_PATH=$RELEASE_PATH/nethermind/src/Nethermind/Nethermind.Runner
OUT=publish

cd $RUNNER_PATH

echo =======================================================
echo Publishing Nethermind Runner for different platforms
echo with v$1+$2
echo =======================================================
echo Nethermind Runner path: $RUNNER_PATH

dotnet publish -c release -r linux-x64 -o $OUT/linux-x64 --sc true -p:Commit=$2 -p:BuildTimestamp=$3 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r linux-arm64 -o $OUT/linux-arm64 --sc true -p:Commit=$2 -p:BuildTimestamp=$3 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r win-x64 -o $OUT/win-x64 --sc true -p:Commit=$2 -p:BuildTimestamp=$3 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r osx-x64 -o $OUT/osx-x64 --sc true -p:Commit=$2 -p:BuildTimestamp=$3 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r osx-arm64 -o $OUT/osx-arm64 --sc true -p:Commit=$2 -p:BuildTimestamp=$3 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true

rm -rf $OUT/linux-x64/Data
rm -rf $OUT/linux-x64/Hive
rm $OUT/linux-x64/*.pdb
cp -r configs $OUT/linux-x64
mkdir $OUT/linux-x64/Data
mkdir $OUT/linux-x64/keystore
cp Data/static-nodes.json $OUT/linux-x64/Data

rm -rf $OUT/linux-arm64/Data
rm -rf $OUT/linux-arm64/Hive
rm $OUT/linux-arm64/*.pdb
cp -r configs $OUT/linux-arm64
mkdir $OUT/linux-arm64/Data
mkdir $OUT/linux-arm64/keystore
cp Data/static-nodes.json $OUT/linux-arm64/Data

rm -rf $OUT/win-x64/Data
rm -rf $OUT/win-x64/Hive
rm $OUT/win-x64/*.pdb
cp -r configs $OUT/win-x64
mkdir $OUT/win-x64/Data
mkdir $OUT/win-x64/keystore
cp Data/static-nodes.json $OUT/win-x64/Data

rm -rf $OUT/osx-x64/Data
rm -rf $OUT/osx-x64/Hive
rm $OUT/osx-x64/*.pdb
cp -r configs $OUT/osx-x64
mkdir $OUT/osx-x64/Data
mkdir $OUT/osx-x64/keystore
cp Data/static-nodes.json $OUT/osx-x64/Data

rm -rf $OUT/osx-arm64/Data
rm -rf $OUT/osx-arm64/Hive
rm $OUT/osx-arm64/*.pdb
cp -r configs $OUT/osx-arm64
mkdir $OUT/osx-arm64/Data
mkdir $OUT/osx-arm64/keystore
cp Data/static-nodes.json $OUT/osx-arm64/Data

mv $OUT/linux-x64 $RELEASE_PATH
mv $OUT/linux-arm64 $RELEASE_PATH
mv $OUT/win-x64 $RELEASE_PATH
mv $OUT/osx-x64 $RELEASE_PATH
mv $OUT/osx-arm64 $RELEASE_PATH

rm -rf $OUT

echo =======================================================
echo Building Nethermind Runner completed
echo =======================================================
