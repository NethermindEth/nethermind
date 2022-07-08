#!/bin/bash
#exit when any command fails
set -e
OSX=nethermind-darwin-amd64
OSX_ARM64=nethermind-darwin-arm64
HOMEBREW_PATH=$RELEASE_DIRECTORY/homebrew-nethermind

cd $RELEASE_DIRECTORY
GIT_SHORT_TAG="$(tail git-tag.txt)"
GIT_HASH="$(tail git-hash.txt)"

echo =======================================================
echo Updating Homebrew package
echo =======================================================

cd $OSX_RELEASE 
darwin_amd64_hash="$(shasum -a 256 $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip | awk '{ print $1}')"
version=$(echo $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip | awk -F- '{ print $4}')
commit=$(echo $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip | awk -F- '{ print $5}')
date=$(echo $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip | awk -F- '{ print substr($6,1,8)}')

cd ..
cd $OSX_ARM64_RELEASE 
darwin_arm64_hash="$(shasum -a 256 $OSX_ARM64-$GIT_SHORT_TAG-$GIT_HASH.zip | awk '{ print $1}')"

cd $HOMEBREW_PATH

sed -i "s/app_version =.*/app_version = '"$version"'/" nethermind.rb
sed -i "s/commit =.*/commit = '"$commit"'/" nethermind.rb
sed -i "s/date =.*/date = '"$date"'/" nethermind.rb
awk -i inplace -v n=1 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$darwin_amd64_hash'\""); } 1' nethermind.rb
awk -i inplace -v n=2 '/sha256/ { if (++count == n) sub(/sha256.*/, "sha256 \"'$darwin_arm64_hash'\""); } 1' nethermind.rb


echo =======================================================
echo Updating Homebrew package completed
echo =======================================================
