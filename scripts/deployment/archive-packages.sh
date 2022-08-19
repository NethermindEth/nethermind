#!/bin/bash
#exit when any command fails
set -e
LIN=nethermind-linux-amd64
OSX=nethermind-darwin-amd64
OSX_ARM64=nethermind-darwin-arm64
WIN=nethermind-windows-amd64
LIN_ARM64=nethermind-linux-arm64
RELEASE_PATH=nethermind/src/Nethermind/Nethermind.Runner/bin/release/net6.0

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_DIRECTORY
VERSION=$1
COMMIT_HASH=$(echo $2 | awk '{print substr($0,0,7);}')
DATE=$(date +'%Y%m%d' -d @$3)

echo =======================================================
echo Copying and packing plugins
echo =======================================================

mkdir -p $LIN_RELEASE/plugins
mkdir -p $OSX_RELEASE/plugins
mkdir -p $OSX_ARM64_RELEASE/plugins
mkdir -p $WIN_RELEASE/plugins
mkdir -p $LIN_ARM64_RELEASE/plugins

cd nethermind/src/Nethermind/Nethermind.Runner
dotnet build -c release Nethermind.Runner.csproj

cd $RELEASE_DIRECTORY

cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{Api,HealthChecks,EthStats,Merge.Plugin,Mev}.dll $LIN_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{Api,HealthChecks,EthStats,Merge.Plugin,Mev}.dll $OSX_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{Api,HealthChecks,EthStats,Merge.Plugin,Mev}.dll $WIN_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{Api,HealthChecks,EthStats,Merge.Plugin,Mev}.dll $LIN_ARM64_RELEASE/plugins
cp $RELEASE_DIRECTORY/$RELEASE_PATH/Nethermind.{Api,HealthChecks,EthStats,Merge.Plugin,Mev}.dll $OSX_ARM64_RELEASE/plugins

cd $LIN_RELEASE && zip -r $LIN-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd $OSX_RELEASE && zip -r $OSX-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd $WIN_RELEASE && zip -r $WIN-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd $LIN_ARM64_RELEASE && zip -r $LIN_ARM64-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd $OSX_ARM64_RELEASE && zip -r $OSX_ARM64-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================
