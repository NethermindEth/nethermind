#!/bin/bash

LIN=nethermind-linux-amd64
OSX=nethermind-darwin-amd64
WIN=nethermind-windows-amd64

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_DIRECTORY
GIT_SHORT_TAG="$(tail git-tag.txt)"
GIT_HASH="$(tail git-hash.txt)"

echo =======================================================
echo Copying and packing plugins
echo =======================================================

mkdir -p plugins
mkdir -p $LIN_RELEASE/plugins
mkdir -p $OSX_RELEASE/plugins
mkdir -p $WIN_RELEASE/plugins

cd nethermind/src/Nethermind/
dotnet build -c Release Nethermind.sln
cd Nethermind.Baseline
dotnet build -c Release

cd $RELEASE_DIRECTORY/plugins
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Analytics/bin/Release/netcoreapp3.1/Nethermind.Analytics.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Cli/bin/Release/netcoreapp3.1/Nethermind.Cli.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Baseline/bin/Release/netcoreapp3.1/Nethermind.Baseline.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Api/bin/Release/netcoreapp3.1/Nethermind.Api.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.HealthChecks/bin/Release/netcoreapp3.1/Nethermind.HealthChecks.dll .

zip -r plugins-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

cd $LIN_RELEASE && zip -r $LIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $OSX_RELEASE && zip -r $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $WIN_RELEASE && zip -r $WIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================