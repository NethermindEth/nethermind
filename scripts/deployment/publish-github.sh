#!/bin/bash
GH_OWNER=NethermindEth
GH_REPO=nethermind
#exit when any command fails
set -e
echo =======================================================
echo Publishing packages to Github Releases
echo =======================================================

cd $RELEASE_DIRECTORY/nethermind
GIT_SHORT_TAG="$(git describe --tags --abbrev=0)"
STATUS=$(curl -s -o /dev/null -w '%{http_code}' https://api.github.com/repos/NethermindEth/nethermind/releases/tags/$GIT_SHORT_TAG)
GIT_SHORT_TAG_FIRST3="${GIT_SHORT_TAG:0:3}"

echo The TAG is $GIT_SHORT_TAG_FIRST3

cd $RELEASE_DIRECTORY
cd plugins && PUB_PLUGINS_FILE="$(basename *.zip)" && cd ..
cd $LIN_RELEASE && PUB_LIN_FILE="$(basename nethermind-linux-amd64-*)" && cd ..
cd $OSX_RELEASE && PUB_OSX_FILE="$(basename nethermind-darwin-amd64-*)" && cd ..
cd $WIN_RELEASE && PUB_WIN_FILE="$(basename nethermind-windows-amd64-*)" && cd ..
cd $LIN_ARM64_RELEASE && PUB_LIN_ARM64_FILE="$(basename nethermind-linux-arm64-*)" && cd ..

if [[ ! -z $GIT_SHORT_TAG ]] && [[ $GIT_SHORT_TAG =~ ^$GIT_SHORT_TAG_FIRST3\d* ]] && [[ $STATUS != 200 ]]; then

API_JSON=$(printf '{"tag_name": "%s","target_commitish": "master","name": "v%s","body": "## What changed?\\n\\n## Running Nethermind:\\n\\nNethermind Launcher is a self-contained app - you do not need to install .NET separately to run it.\\n\\n**PPA**\\n(Tested on Ubuntu Series: `Focal`, `Bionic`, `Xenial` and `Trusty`)\\n1. `sudo add-apt-repository ppa:nethermindeth/nethermind`\\n1. `sudo apt install nethermind`\\n1. To execute the launcher\\n``nethermind``\\n1. To execute the runner\\n``nethermind --config mainnet``\\n\\n**Homebrew**\\n1. `brew tap nethermindeth/nethermind`\\n1. `brew install nethermind`\\n1. To execute the launcher\\n``nethermind-launcher``\\n1. To execute the runner\\n``nethermind --config mainnet``\\n\\n**Docker**\\n1. `docker pull nethermind/nethermind`\\n1. `docker run -it nethermind/nethermind`\\n\\n**Linux**\\n\\n1. `sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6 unzip`\\n1. `wget https://github.com/NethermindEth/nethermind/releases/download/%s/%s`\\n1. `unzip %s -d nethermind`\\n1. `cd nethermind`\\n1. `./Nethermind.Launcher`\\n1. select desired configuration\\n\\n**Linux Arm64**\\n\\n1. `sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6 librocksdb5.17 unzip`\\n1. `wget https://github.com/NethermindEth/nethermind/releases/download/%s/%s`\\n1. `unzip %s -d nethermind`\\n1. `cd nethermind`\\n1. `./Nethermind.Launcher`\\n1. select desired configuration\\n\\n**Windows**\\n\\n1. download windows package `%s`\\n1. unzip the file\\n1. run `Nethermind.Launcher.exe`\\n1. select desired configuration\\n\\n**macOS**\\n\\n1. `brew install rocksdb`\\n1. download darwin package `%s`\\n1. unzip the file\\n1. run `Nethermind.Launcher`\\n1. select desired configuration", "draft" :false, "prerelease": true}' $GIT_SHORT_TAG $GIT_SHORT_TAG $GIT_SHORT_TAG $PUB_LIN_FILE $PUB_LIN_FILE $GIT_SHORT_TAG $PUB_LIN_ARM64_FILE $PUB_LIN_ARM64_FILE $PUB_WIN_FILE $PUB_OSX_FILE)

curl --data "$API_JSON" -H "Authorization: token $GITHUB_TOKEN" https://api.github.com/repos/$GH_OWNER/$GH_REPO/releases
RELEASE_URL=https://github.com/$GH_OWNER/$GH_REPO/releases/tag/$GIT_SHORT_TAG

echo =======================================================
echo Uploading Linux releases
echo =======================================================

./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=$RELEASE_DIRECTORY/$LIN_RELEASE/$PUB_LIN_FILE
./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=$RELEASE_DIRECTORY/$LIN_ARM64_RELEASE/$PUB_LIN_ARM64_FILE

echo =======================================================
echo Finished uploading Linux release
echo =======================================================

echo =======================================================
echo Uploading Windows release
echo =======================================================

./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=$RELEASE_DIRECTORY/$WIN_RELEASE/$PUB_WIN_FILE

echo =======================================================
echo Finished uploading Windows release
echo =======================================================

echo =======================================================
echo Uploading Darwin release
echo ======================================================

./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=$RELEASE_DIRECTORY/$OSX_RELEASE/$PUB_OSX_FILE

echo =======================================================
echo Finished uploading Darwin release
echo =======================================================

echo =======================================================
echo Uploading plugins package
echo =======================================================

./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=$RELEASE_DIRECTORY/plugins/$PUB_PLUGINS_FILE

echo =======================================================
echo Finished uploading plugins package
echo =======================================================

echo =======================================================
echo Packages have been successfully published on $RELEASE_URL
echo =======================================================

else
    echo =======================================================
    echo Incorrect tag or release already exists in repository
    echo =======================================================
    exit 1
fi