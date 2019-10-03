#!/bin/bash
export GPG_TTY=$(tty)
RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64
GH_OWNER=NethermindEth
GH_REPO=nethermind

echo =======================================================
echo Publishing packages to Github Releases
echo =======================================================

cd $RELEASE_DIRECTORY
GIT_SHORT_TAG="$(tail git-tag.txt)"
GIT_VERSION="$(head -c 5 git-tag.txt)"
STATUS=$(curl -s -o /dev/null -w '%{http_code}' https://api.github.com/repos/NethermindEth/nethermind/releases/tags/$GIT_SHORT_TAG)

cd $LIN_RELEASE && LIN_FILE="$(basename nethermind-linux-amd64-*)" && cd ..
cd $OSX_RELEASE && OSX_FILE="$(basename nethermind-darwin-amd64-*)" && cd ..
cd $WIN_RELEASE && WIN_FILE="$(basename nethermind-windows-amd64-*)" && cd ..

if [[ $GIT_SHORT_TAG =~ ^1.1\d* ]] && [[ $STATUS != 200 ]]; then

API_JSON=$(printf '{"tag_name": "%s","target_commitish": "master","name": "v%s","body": "## Running Nethermind:\\n\\nNethermind Launcher is a self-contained app - you do not need to install .NET separately to run it.\\n\\n**Linux**\\n\\n1. `sudo apt-get update && sudo apt-get install libsnappy-dev libc6-dev libc6 unzip`\\n1. `wget https://github.com/NethermindEth/nethermind/releases/download/%s/%s`\\n1. `unzip %s -d`\\n1. `cd nethermind`\\n1. `./Nethermind.Launcher`\\n1. Select desired configuration\\n\\n**Windows**\\n\\n1. download windows package `%s`\\n1. unzip the file\\n1. run Nethermind.Launcher.exe\\n1. select desired configuration\\n\\n**macOS**\\n\\n1. download darwin package `%s`\\n1. unzip the file\\n1. run Nethermind.Launcher\\n1. select desired configuration", "draft" :false, "prerelease": true}' $GIT_SHORT_TAG $GIT_VERSION $GIT_SHORT_TAG $LIN_FILE $LIN_FILE $WIN_FILE)

curl --data "$API_JSON" https://api.github.com/repos/$GH_OWNER/$GH_REPO/releases?access_token=$GH_TOKEN
RELEASE_URL=https://github.com/$GH_OWNER/$GH_REPO/releases/tag/$GIT_SHORT_TAG

cd ~/repo_pub/
echo =======================================================
echo Uploading Linux release
echo =======================================================

./upload-github-release-asset.sh github_api_token=$GH_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=nethermind-packages/$LIN_RELEASE/$LIN_FILE

echo =======================================================
echo Finished uploading Linux release
echo =======================================================

echo =======================================================
echo Uploading Windows release
echo =======================================================

./upload-github-release-asset.sh github_api_token=$GH_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=nethermind-packages/$WIN_RELEASE/$WIN_FILE

echo =======================================================
echo Finished uploading Windows release
echo =======================================================

echo =======================================================
echo Uploading Darwin release
echo =======================================================

./upload-github-release-asset.sh github_api_token=$GH_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_SHORT_TAG filename=nethermind-packages/$OSX_RELEASE/$OSX_FILE

echo =======================================================
echo Finished uploading Darwin release
echo =======================================================

echo =======================================================
echo Packages have been successfully published on $RELEASE_URL
echo =======================================================

else
echo ======================================================= 
echo Incorrect tag or release already exists in repository
echo =======================================================
fi
