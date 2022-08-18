#!/bin/bash
#exit when any command fails
set -e
OSX=nethermind-darwin-amd64
OSX_ARM64=nethermind-darwin-arm64
HOMEBREW_PATH=$RELEASE_DIRECTORY/homebrew-nethermind

cd $RELEASE_DIRECTORY
DATE=$(date +'%Y%m%d' -d @$TIMESTAMP)
COMMIT_HASH=$(echo $HASH | awk '{print substr($0,0,7);}')
echo =======================================================
echo Updating Homebrew package
echo =======================================================

cd $OSX_RELEASE 
darwin_amd64_hash="$(shasum -a 256 $OSX-$VERSION-$COMMIT_HASH-$DATE.zip | awk '{ print $1}')"
cd ..
cd $OSX_ARM64_RELEASE 
darwin_arm64_hash="$(shasum -a 256 $OSX_ARM64-$VERSION-$COMMIT_HASH-$DATE.zip | awk '{ print $1}')"

cd $HOMEBREW_PATH

sed -i "s/app_version =.*/app_version = '"$VERSION"'/" nethermind.rb
sed -i "s/commit =.*/commit = '"$COMMIT_HASH"'/" nethermind.rb
sed -i "s/date =.*/date = '"$DATE"'/" nethermind.rb
awk -i inplace -v n=1 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$darwin_amd64_hash'\""); } 1' nethermind.rb
awk -i inplace -v n=2 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$darwin_arm64_hash'\""); } 1' nethermind.rb


echo =======================================================
echo Updating Homebrew package completed
echo =======================================================
