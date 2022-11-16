#!/bin/bash
#exit when any command fails
set -e

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_PATH && mkdir $PACKAGE_DIR

cd linux-x64 && zip -r ../$PACKAGE_DIR/$PACKAGE_PREFIX-linux-x64.zip . && cd ..
cd linux-arm64 && zip -r ../$PACKAGE_DIR/$PACKAGE_PREFIX-linux-arm64.zip . && cd ..
cd win-x64 && zip -r ../$PACKAGE_DIR/$PACKAGE_PREFIX-win-x64.zip . && cd ..
cd osx-x64 && zip -r ../$PACKAGE_DIR/$PACKAGE_PREFIX-osx-x64.zip . && cd ..
cd osx-arm64 && zip -r ../$PACKAGE_DIR/$PACKAGE_PREFIX-osx-arm64.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================
