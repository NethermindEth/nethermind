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
cp $CLI_PATH/$PUBLISH_PATH/$LINUX/System.Runtime.dll $OUT/$LIN_RELEASE/System.Runtime.dll

dotnet publish -c release -r $OSX -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$OSX_RELEASE
cp $CLI_PATH/$PUBLISH_PATH/$OSX/System.Runtime.dll $OUT/$OSX_RELEASE/System.Runtime.dll

dotnet publish -c release -r $WIN10 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$WIN_RELEASE
cp $CLI_PATH/$PUBLISH_PATH/$WIN10/System.Runtime.dll $OUT/$WIN_RELEASE/System.Runtime.dll

dotnet publish -c release -r $LINUX_ARM -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$LIN_ARM_RELEASE
cp $CLI_PATH/$PUBLISH_PATH/$LINUX_ARM/System.Runtime.dll $OUT/$LIN_ARM_RELEASE/System.Runtime.dll

dotnet publish -c release -r $LINUX_ARM64 -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -o $OUT/$LIN_ARM64_RELEASE
cp $CLI_PATH/$PUBLISH_PATH/$LINUX_ARM64/System.Runtime.dll $OUT/$LIN_ARM64_RELEASE/System.Runtime.dll

echo =======================================================
echo Packing Nethermind Cli for different platforms...
echo =======================================================

rm $OUT/$LIN_RELEASE/*.pdb
rm $OUT/$OSX_RELEASE/*.pdb
rm $OUT/$WIN_RELEASE/*.pdb
rm $OUT/$LIN_ARM64_RELEASE/*.pdb
rm $OUT/$LIN_ARM_RELEASE/*.pdb

cp -r $OUT/$LIN_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$OSX_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$WIN_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$LIN_ARM64_RELEASE $RELEASE_DIRECTORY
cp -r $OUT/$LIN_ARM_RELEASE $RELEASE_DIRECTORY

rm -rf $OUT

echo =======================================================
echo Building Nethermind Cli completed
echo =======================================================