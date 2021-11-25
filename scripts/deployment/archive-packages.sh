#!/bin/bash
#exit when any command fails
set -e
LIN=nethermind-linux-amd64
OSX=nethermind-darwin-amd64
OSX_ARM64=nethermind-darwin-arm64
WIN=nethermind-windows-amd64
LIN_ARM64=nethermind-linux-arm64
RELEASE_PATH=nethermind/src/Nethermind/Nethermind.Runner/bin/Release/net6.0

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_DIRECTORY
GIT_SHORT_TAG="$(tail git-tag.txt)"
GIT_HASH="$(tail git-hash.txt)"

echo =======================================================
echo Copying and packing plugins
echo =======================================================

mkdir -p $LIN_RELEASE/plugins
mkdir -p $OSX_RELEASE/plugins
mkdir -p $OSX_ARM64_RELEASE/plugins
mkdir -p $WIN_RELEASE/plugins
mkdir -p $LIN_ARM64_RELEASE/plugins

cd nethermind/src/Nethermind/
dotnet build -c Release Nethermind.sln

cd $RELEASE_DIRECTORY

cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{HealthChecks,Merge.Plugin,Mev}.dll $LIN_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{HealthChecks,Merge.Plugin,Mev}.dll $OSX_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{HealthChecks,Merge.Plugin,Mev}.dll $WIN_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{HealthChecks,Merge.Plugin,Mev}.dll $LIN_ARM64_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{HealthChecks,Merge.Plugin,Mev}.dll $OSX_ARM64_RELEASE/plugins

cd $LIN_RELEASE && zip -r $LIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $OSX_RELEASE && zip -r $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $WIN_RELEASE && zip -r $WIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $LIN_ARM64_RELEASE && zip -r $LIN_ARM64-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $OSX_ARM64_RELEASE && zip -r $OSX_ARM64-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================