#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

echo "Archiving Nethermind packages"

mkdir $PACKAGE_DIR
cd $PUB_DIR

cd linux-x64 && zip -ry $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-linux-x64.zip . && cd ..
cd ref && zip -r $GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-ref-assemblies.zip . && cd ..

echo "Archiving completed"
