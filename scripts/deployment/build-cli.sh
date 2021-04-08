#!/bin/bash
#exit when any command fails
set -e
CLI_PATH=$RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Cli
PUBLISH_PATH=bin/release/net5.0
OUT=out

cd $CLI_PATH

echo =======================================================
echo Publishing Nethermind Cli for different platforms...
echo =======================================================
echo Nethermind Cli path: $CLI_PATH

dotnet publish -c release -r $LINUX -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$LIN_RELEASE
dotnet publish -c release -r $OSX -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$OSX_RELEASE
dotnet publish -c release -r $WIN10 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$WIN_RELEASE
dotnet publish -c release -r $LINUX_ARM64 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$LIN_ARM64_RELEASE

echo =======================================================
echo Packing Nethermind Cli for different platforms...
echo =======================================================

rm $OUT/$LIN_RELEASE/*.pdb
rm $OUT/$OSX_RELEASE/*.pdb
rm $OUT/$WIN_RELEASE/*.pdb
rm $OUT/$LIN_ARM64_RELEASE/*.pdb

cp -r $OUT/$LIN_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$OSX_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$WIN_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$LIN_ARM64_RELEASE $RELEASE_DIRECTORY

rm -rf $OUT

echo =======================================================
echo Building Nethermind Cli completed
echo =======================================================