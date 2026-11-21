#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

echo "Archiving Nethermind packages"

mkdir $PACKAGE_DIR
cd $PUB_DIR

# Reproducible archives: sorted entries, no extra attributes, and DOS times independent of the runner's time zone
export TZ=UTC

archive() {
  (cd "$1" && find . -mindepth 1 | LC_ALL=C sort | zip -qX $3 "$GITHUB_WORKSPACE/$PACKAGE_DIR/$PACKAGE_PREFIX-$2.zip" -@)
}

archive linux-arm64 linux-arm64 -y
archive linux-x64 linux-x64 -y
archive osx-arm64 macos-arm64
archive osx-x64 macos-x64
archive win-arm64 windows-arm64
archive win-x64 windows-x64
archive ref ref-assemblies

echo "Archiving completed"
