#!/bin/bash
#exit when any command fails
set -e

LAUNCHER_PATH=$RELEASE_PATH/launcher
APP_NAME=Nethermind.Launcher

echo =======================================================
echo Building Nethermind Launcher
echo =======================================================

cd $LAUNCHER_PATH

npm i
pkg index.js -t latest-linux-x64 -o $APP_NAME && mv $APP_NAME $RELEASE_PATH/linux-x64
pkg index.js -t latest-win-x64 -o $APP_NAME.exe && mv $APP_NAME.exe $RELEASE_PATH/win-x64
pkg index.js -t latest-macos-x64 -o $APP_NAME && mv $APP_NAME $RELEASE_PATH/osx-x64 && cp $RELEASE_PATH/osx-x64/$APP_NAME $RELEASE_PATH/osx-arm64/$APP_NAME

echo =======================================================
echo Building Nethermind Launcher completed
echo =======================================================
