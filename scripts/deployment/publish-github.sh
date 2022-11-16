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
PACKAGE_PATH=$RELEASE_PATH/$PACKAGE_DIR

echo The TAG is $GIT_TAG_3

cd $PACKAGE_PATH
LINUX_X64_FILE=$(basename *linux-x64*)
LINUX_ARM64_FILE=$(basename *linux-arm64*)
WIN_X64_FILE=$(basename *win-x64*)
OSX_X64_FILE=$(basename *osx-x64*)
OSX_ARM64_FILE=$(basename *osx-arm64*)

if [[ ! -z $GIT_TAG ]] && [[ $GIT_TAG =~ ^$GIT_TAG_3\d* ]] && [[ $STATUS != 200 ]]; then

  API_JSON=$(printf '{"tag_name": "%s", "target_commitish": "master", "name": "v%s", "body": "## Release notes\\n\\n", "draft": true, "prerelease": false}' $GIT_TAG $GIT_TAG)

  curl --data "$API_JSON" -H "Authorization: token $GITHUB_TOKEN" https://api.github.com/repos/$GH_OWNER/$GH_REPO/releases

  RELEASE_URL=https://github.com/$GH_OWNER/$GH_REPO/releases/tag/$GIT_TAG

  echo =======================================================
  echo Uploading Linux releases
  echo =======================================================

  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$PACKAGE_PATH/$LINUX_X64_FILE
  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$PACKAGE_PATH/$LINUX_ARM64_FILE

  echo =======================================================
  echo Finished uploading Linux release
  echo =======================================================

  echo =======================================================
  echo Uploading Windows release
  echo =======================================================

  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$PACKAGE_PATH/$WIN_X64_FILE

  echo =======================================================
  echo Finished uploading Windows release
  echo =======================================================

  echo =======================================================
  echo Uploading macOS release
  echo ======================================================

  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$PACKAGE_PATH/$OSX_X64_FILE
  ./nethermind/scripts/deployment/upload-github-release-asset.sh github_api_token=$GITHUB_TOKEN owner=$GH_OWNER repo=$GH_REPO tag=$GIT_TAG filename=$PACKAGE_PATH/$OSX_ARM64_FILE

  echo =======================================================
  echo Finished uploading macOS release
  echo =======================================================

  echo =======================================================
  echo Packages have been successfully published to $RELEASE_URL
  echo =======================================================

else
  echo =======================================================
  echo Incorrect tag or release already exists in repository
  echo =======================================================
  exit 1
fi
