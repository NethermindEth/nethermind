#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

echo "Archiving Nethermind packages"

cd $GITHUB_WORKSPACE
mkdir $PACKAGE_DIR
cd $PUB_DIR

cd linux-x64 && zip -r -y $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-linux-x64.zip . && cd ..
cd linux-arm64 && zip -r -y $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-linux-arm64.zip . && cd ..
cd win-x64 && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-windows-x64.zip . && cd ..
cd osx-x64 && zip -r -y $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-macos-x64.zip . && cd ..
cd osx-arm64 && zip -r -y $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-macos-arm64.zip . && cd ..
cd ref && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-ref-assemblies.zip . && cd ..

echo "Archiving completed"
