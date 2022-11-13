#!/bin/bash
#exit when any command fails
set -e

CLI_PATH=$RELEASE_PATH/nethermind/src/Nethermind/Nethermind.Cli
OUT=publish

cd $CLI_PATH

echo =======================================================
echo Publishing Nethermind Cli for different platforms...
echo =======================================================
echo Nethermind Cli path: $CLI_PATH

dotnet publish -c release -r linux-x64 -o $OUT/linux-x64 --sc true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r linux-arm64 -o $OUT/linux-arm64 --sc true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r win-x64 -o $OUT/win-x64 --sc true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r osx-x64 -o $OUT/osx-x64 --sc true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true
dotnet publish -c release -r osx-arm64 -o $OUT/osx-arm64 --sc true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true

echo =======================================================
echo Packing Nethermind Cli for different platforms...
echo =======================================================

rm $OUT/linux-x64/*.pdb
rm $OUT/linux-arm64/*.pdb
rm $OUT/win-x64/*.pdb
rm $OUT/osx-x64/*.pdb
rm $OUT/osx-arm64/*.pdb

cp -r $OUT/linux-x64 $RELEASE_PATH
cp -r $OUT/linux-arm64 $RELEASE_PATH
cp -r $OUT/win-x64 $RELEASE_PATH
cp -r $OUT/osx-x64 $RELEASE_PATH
cp -r $OUT/osx-arm64 $RELEASE_PATH

rm -rf $OUT

echo =======================================================
echo Building Nethermind Cli completed
echo =======================================================
