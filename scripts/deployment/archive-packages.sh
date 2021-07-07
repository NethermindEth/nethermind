#!/bin/bash
#exit when any command fails
set -e
LIN=nethermind-linux-amd64
OSX=nethermind-darwin-amd64
WIN=nethermind-windows-amd64
LIN_ARM64=nethermind-linux-arm64

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
rm -rf $LIN_RELEASE/ndm-plugins
mkdir -p $OSX_RELEASE/plugins
rm -rf $OSX_RELEASE/ndm-plugins
mkdir -p $WIN_RELEASE/plugins
rm -rf $WIN_RELEASE/ndm-plugins
mkdir -p $LIN_ARM64_RELEASE/plugins
rm -rf $LIN_ARM64_RELEASE/ndm-plugins

cd nethermind/src/Nethermind/
dotnet build -c Release Nethermind.sln
cd Nethermind.Baseline
dotnet build -c Release

cd $RELEASE_DIRECTORY/plugins

cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Analytics/bin/Release/net5.0/Nethermind.Analytics.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Baseline/bin/Release/net5.0/Nethermind.Baseline.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Api/bin/Release/net5.0/Nethermind.Api.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.HealthChecks/bin/Release/net5.0/Nethermind.HealthChecks.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Runner/bin/Release/net5.0/Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Runner/bin/Release/net5.0/Microsoft.Extensions.Diagnostics.HealthChecks.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Merge.Plugin/bin/Release/net5.0/Nethermind.Merge.Plugin.dll .
cp $RELEASE_DIRECTORY/nethermind/src/Nethermind/Nethermind.Mev/bin/Release/net5.0/Nethermind.Mev.dll .

zip -r plugins-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

cp $RELEASE_DIRECTORY/plugins/Nethermind.HealthChecks.dll $LIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.HealthChecks.dll $OSX_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.HealthChecks.dll $WIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.HealthChecks.dll $LIN_ARM64_RELEASE/plugins/

cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions.dll $LIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions.dll $OSX_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions.dll $WIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions.dll $LIN_ARM64_RELEASE/plugins/

cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.dll $LIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.dll $OSX_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.dll $WIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Microsoft.Extensions.Diagnostics.HealthChecks.dll $LIN_ARM64_RELEASE/plugins/

cp $RELEASE_DIRECTORY/plugins/Nethermind.Api.dll $LIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Api.dll $OSX_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Api.dll $WIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Api.dll $LIN_ARM64_RELEASE/plugins/

cp $RELEASE_DIRECTORY/plugins/Nethermind.Merge.Plugin.dll $LIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Merge.Plugin.dll $OSX_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Merge.Plugin.dll $WIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Merge.Plugin.dll $LIN_ARM64_RELEASE/plugins/

cp $RELEASE_DIRECTORY/plugins/Nethermind.Mev.dll $LIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Mev.dll $OSX_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Mev.dll $WIN_RELEASE/plugins/
cp $RELEASE_DIRECTORY/plugins/Nethermind.Mev.dll $LIN_ARM64_RELEASE/plugins/

cd $LIN_RELEASE && zip -r $LIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $OSX_RELEASE && zip -r $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $WIN_RELEASE && zip -r $WIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $LIN_ARM64_RELEASE && zip -r $LIN_ARM64-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================