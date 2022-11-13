#!/bin/bash
#exit when any command fails
set -e

COMMIT_HASH=$(echo $HASH | awk '{print substr($0,0,7);}')
DATE=$(date +'%Y%m%d' -d @$TIMESTAMP)
HOMEBREW_PATH=$RELEASE_PATH/homebrew-nethermind

echo =======================================================
echo Updating Homebrew package
echo =======================================================

cd $RELEASE_PATH

cd $OSX_X64_PKG
osx_x64_hash="$(shasum -a 256 $OSX_X64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip | awk '{ print $1}')"
cd ..
cd $OSX_ARM64_PKG
osx_arm64_hash="$(shasum -a 256 $OSX_ARM64_PKG-$VERSION-$COMMIT_HASH-$DATE.zip | awk '{ print $1}')"

cd $HOMEBREW_PATH

sed -i "s/app_version =.*/app_version = '"$VERSION"'/" nethermind.rb
sed -i "s/commit =.*/commit = '"$COMMIT_HASH"'/" nethermind.rb
sed -i "s/date =.*/date = '"$DATE"'/" nethermind.rb
awk -i inplace -v n=1 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$osx_x64_hash'\""); } 1' nethermind.rb
awk -i inplace -v n=2 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$osx_arm64_hash'\""); } 1' nethermind.rb


echo =======================================================
echo Updating Homebrew package completed
echo =======================================================
