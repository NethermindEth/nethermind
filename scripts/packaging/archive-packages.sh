RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64
LIN=nethermind-linux-amd64
OSX=nethermind-darwin-amd64
WIN=nethermind-windows-amd64

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

echo =======================================================
echo Generating git-hash files

cd nethermind
GIT="$(git rev-parse --short=7 HEAD)"
GIT_TAG="$(git describe --tags)"
GIT_SHORT_TAG="$(echo $GIT_TAG | cut -d- -f1)"
cd ..
cd $RELEASE_DIRECTORY
touch git-hash.txt
echo $GIT > git-hash.txt
GIT_HASH="$(tail git-hash.txt)"

echo =======================================================
echo Success, git-hash files have been created
echo =======================================================

cd $LIN_RELEASE && zip -r $LIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $OSX_RELEASE && zip -r $OSX-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..
cd $WIN_RELEASE && zip -r $WIN-$GIT_SHORT_TAG-$GIT_HASH.zip . && cd ..

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================