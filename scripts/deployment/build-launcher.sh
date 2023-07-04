#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

app_name=Nethermind.Launcher
output_path=$GITHUB_WORKSPACE/$PUB_DIR

echo "Building Nethermind Launcher"

cd $GITHUB_WORKSPACE/launcher

npm i
pkg index.js -t latest-linux-x64 -o $app_name && mv $app_name $output_path/linux-x64
pkg index.js -t latest-win-x64 -o $app_name.exe && mv $app_name.exe $output_path/win-x64
pkg index.js -t latest-macos-x64 -o $app_name && mv $app_name $output_path/osx-x64 && \
  cp $output_path/osx-x64/$app_name $output_path/osx-arm64/$app_name

echo "Build completed"
