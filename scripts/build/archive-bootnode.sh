#!/bin/bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

: "${PACKAGE_DIR:?}"
: "${PACKAGE_PREFIX:?}"
: "${PUB_DIR:?}"

workspace=${GITHUB_WORKSPACE:-$(pwd)}
package_path=$workspace/$PACKAGE_DIR

echo "Archiving Nethermind.Bootnode packages"

mkdir -p "$package_path"
cd "$PUB_DIR"

tar -czf "$package_path/$PACKAGE_PREFIX-linux-arm64.tar.gz" -C linux-arm64 .
tar -czf "$package_path/$PACKAGE_PREFIX-linux-x64.tar.gz" -C linux-x64 .
tar -czf "$package_path/$PACKAGE_PREFIX-macos-arm64.tar.gz" -C osx-arm64 .
tar -czf "$package_path/$PACKAGE_PREFIX-macos-x64.tar.gz" -C osx-x64 .
cd win-x64 && zip -r "$package_path/$PACKAGE_PREFIX-windows-x64.zip" . && cd ..

cd "$package_path"
sha256sum *.tar.gz *.zip > "$PACKAGE_PREFIX-SHA256SUMS"

echo "Archiving completed"
