#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

APP_NAME=Nethermind.Launcher
LAUNCHER_PATH=$GITHUB_WORKSPACE/launcher
OUTPUT_PATH=$GITHUB_WORKSPACE/$PUB_DIR

echo "Building Nethermind Launcher"

cd $LAUNCHER_PATH

npm i
pkg index.js -t latest-linux-x64 -o $APP_NAME && mv $APP_NAME $OUTPUT_PATH/linux-x64
pkg index.js -t latest-win-x64 -o $APP_NAME.exe && mv $APP_NAME.exe $OUTPUT_PATH/win-x64
pkg index.js -t latest-macos-x64 -o $APP_NAME && mv $APP_NAME $OUTPUT_PATH/osx-x64 && \
  cp $OUTPUT_PATH/osx-x64/$APP_NAME $OUTPUT_PATH/osx-arm64/$APP_NAME

echo "Build completed"
