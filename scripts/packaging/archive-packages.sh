RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64
LIN=nethermind-linux-amd64
OSX=nethermind-darwin-amd64
WIN=nethermind-windows-amd64
PPA=nethermind-ppa
LATEST=latest


echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_DIRECTORY
GIT_SHORT_TAG="$(tail git-tag.txt)"
GIT_HASH="$(tail git-hash.txt)"

cd ~/repo_pub/$RELEASE_DIRECTORY
cd $LIN_RELEASE && zip -r $LIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $OSX_RELEASE && zip -r $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $WIN_RELEASE && zip -r $WIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

mkdir $PPA
cd $PPA
mv $LIN_RELEASE/$LIN-$GIT_SHORT_TAG-$GIT_HASH.zip .
mv $LIN-$GIT_SHORT_TAG-$GIT_HASH.zip $PPA-$LATEST.zip

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================

