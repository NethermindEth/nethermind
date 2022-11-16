#!/bin/bash
#exit when any command fails
set -e

HOMEBREW_PATH=$RELEASE_PATH/homebrew-nethermind

echo =======================================================
echo Updating Homebrew package
echo =======================================================

cd $RELEASE_PATH/$PACKAGE_DIR

osx_x64_hash="$(shasum -a 256 $PACKAGE_PREFIX-osx-x64.zip | awk '{print $1}')"
osx_arm64_hash="$(shasum -a 256 $PACKAGE_PREFIX-osx-arm64.zip | awk '{print $1}')"

cd $HOMEBREW_PATH

sed -i "s/app_version =.*/app_version = '"$VERSION"'/" nethermind.rb
sed -i "s/package_prefix =.*/package_prefix = '"PACKAGE_PREFIX"'/" nethermind.rb
awk -i inplace -v n=1 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$osx_x64_hash'\""); } 1' nethermind.rb
awk -i inplace -v n=2 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$osx_arm64_hash'\""); } 1' nethermind.rb

echo =======================================================
echo Updating Homebrew package completed
echo =======================================================
