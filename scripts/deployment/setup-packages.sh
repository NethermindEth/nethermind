#!/bin/bash
#exit when any command fails
set -e
echo =======================================================
echo Setting up Nethermind packages
echo =======================================================

DATE=`date +%Y%m%d`

cd $RELEASE_DIRECTORY
mkdir $LIN_RELEASE
mkdir $OSX_RELEASE
mkdir $WIN_RELEASE
mkdir $LIN_ARM64_RELEASE


echo =======================================================
echo Creating  git-hash and git-tag files
echo =======================================================

cd $RELEASE_DIRECTORY/nethermind
GIT="$(git rev-parse --short=7 HEAD)"
GIT_TAG_SHORT="$(git describe --tags --abbrev=0)"
GIT_TAG_LONG="$(git describe --tags --long)"
cd ..
touch git-hash.txt
touch git-tag.txt
touch git-tag-long.txt

echo $GIT-$DATE > git-hash.txt
echo ${GIT_TAG_SHORT} > git-tag.txt
echo ${GIT_TAG_LONG} > git-tag-long.txt

echo =======================================================
echo Success, git-hash and git-tag  files have been created
echo =======================================================

echo =======================================================
echo Setting up Nethermind packages completed
echo =======================================================