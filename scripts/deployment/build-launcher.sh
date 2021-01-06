#!/bin/bash
#exit when any command fails
set -e
LAUNCHER_PATH=$RELEASE_DIRECTORY/launcher
APP_NAME=Nethermind.Launcher

echo =======================================================
echo Building Nethermind Launcher
echo =======================================================

cd $LAUNCHER_PATH
npm i
pkg index.js -t node13-linux -o $APP_NAME && mv $APP_NAME $RELEASE_DIRECTORY/$LIN_RELEASE
pkg index.js -t node13-osx -o $APP_NAME && mv $APP_NAME $RELEASE_DIRECTORY/$OSX_RELEASE
pkg index.js -t node13-win -o $APP_NAME.exe && mv $APP_NAME.exe $RELEASE_DIRECTORY/$WIN_RELEASE

ncc build index.js -o build
cp build/index.js $RELEASE_DIRECTORY/$LIN_ARM64_RELEASE/$APP_NAME.js
cp build/index.js $RELEASE_DIRECTORY/$LIN_ARM_RELEASE/$APP_NAME.js

echo =======================================================
echo Building Nethermind Launcher completed
echo =======================================================