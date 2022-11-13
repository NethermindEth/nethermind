#!/bin/bash
#exit when any command fails
set -e

VERSION=$1
COMMIT_HASH=$(echo $2 | awk '{print substr($0,0,7);}')
DATE=$(date +'%Y%m%d' -d @$3)

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_PATH

cd linux-x64 && zip -r $LINUX_X64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd linux-arm64 && zip -r $LINUX_ARM64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd win-x64 && zip -r $WIN_X64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd osx-x64 && zip -r $OSX_X64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..
cd osx-arm64 && zip -r $OSX_ARM64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================
