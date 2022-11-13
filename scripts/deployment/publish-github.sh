#!/bin/bash
GH_OWNER=NethermindEth
GH_REPO=nethermind
#exit when any command fails
set -e

echo =======================================================
echo Publishing packages to Github Releases
echo =======================================================

cd $RELEASE_PATH/nethermind
STATUS=$(curl -s -o /dev/null -w '%{http_code}' https://api.github.com/repos/NethermindEth/nethermind/releases/tags/$GIT_TAG)
GIT_TAG_3="${GIT_TAG:0:3}"

echo The TAG is $GIT_TAG_3

cd $RELEASE_PATH
cd $LINUX_X64_PKG && LINUX_X64_FILE="$(basename $LINUX_X64_PKG-*)" && cd ..
cd $LINUX_ARM64_PKG && LINUX_ARM64_FILE="$(basename $LINUX_ARM64_PKG-*)" && cd ..
cd $WIN_X64_PKG && WIN_X64_FILE="$(basename $WIN_X64_PKG-*)" && cd ..
cd $OSX_X64_PKG && OSX_X64_FILE="$(basename $OSX_X64_PKG-*)" && cd ..
cd $OSX_ARM64_PKG && OSX_ARM64_FILE="$(basename $OSX_ARM64_PKG-*)" && cd ..

if [[ ! -z $GIT_TAG ]] && [[ $GIT_TAG =~ ^$GIT_TAG_3\d* ]] && [[ $STATUS != 200 ]]; then

  API_JSON=$(printf '{"tag_name": "%s", "target_commitish": "master", "name": "v%s", "body": "## Release notes\\n\\n", "draft": true, "prerelease": false}' $GIT_TAG $GIT_TAG)

  curl --data "$API_JSON" -H "Authorization: token $GITHUB_TOKEN" https://api.github.com/repos/$GH_OWNER/$GH_REPO/releases
  RELEASE_URL=https://github.com/$GH_OWNER/$GH_REPO/releases/tag/$GIT_TAG

  echo =======================================================
  echo Uploading Linux releases
  echo =======================================================

  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$RELEASE_PATH/$LINUX_X64_PKG/$LINUX_X64_FILE
  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$RELEASE_PATH/$LINUX_ARM64_PKG/$LINUX_ARM64_FILE

  echo =======================================================
  echo Finished uploading Linux release
  echo =======================================================

  echo =======================================================
  echo Uploading Windows release
  echo =======================================================

  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$RELEASE_PATH/$WIN_X64_PKG/$WIN_X64_FILE

  echo =======================================================
  echo Finished uploading Windows release
  echo =======================================================

  echo =======================================================
  echo Uploading macOS release
  echo ======================================================

  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$RELEASE_PATH/$OSX_X64_PKG/$OSX_X64_FILE
  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$RELEASE_PATH/$OSX_ARM64_PKG/$OSX_ARM64_FILE

  echo =======================================================
  echo Finished uploading macOS release
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
