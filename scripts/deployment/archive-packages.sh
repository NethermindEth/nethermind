#!/bin/bash

set -e

echo "Archiving Nethermind packages"

cd $GITHUB_WORKSPACE
mkdir $PACKAGE_DIR
cd $PUB_DIR

cd linux-x64 && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-linux-x64.zip . && cd ..
cd linux-arm64 && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-linux-arm64.zip . && cd ..
cd win-x64 && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-windows-x64.zip . && cd ..
cd osx-x64 && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-macos-x64.zip . && cd ..
cd osx-arm64 && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-macos-arm64.zip . && cd ..

echo "Archiving completed"
