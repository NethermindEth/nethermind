RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

echo =======================================================
echo Setting up Nethermind packages
echo =======================================================

DATE=`date +%Y%m%d`

rm -rf $RELEASE_DIRECTORY
mkdir -p $RELEASE_DIRECTORY/$LIN_RELEASE
mkdir -p $RELEASE_DIRECTORY/$OSX_RELEASE
mkdir -p $RELEASE_DIRECTORY/$WIN_RELEASE
cd nethermind
GIT="$(git rev-parse --short=7 HEAD)"
GIT_TAG="$(git describe --tags)"
GIT_SHORT_TAG="$(echo $GIT_TAG | cut -d- -f1)"
cd ..
cd $RELEASE_DIRECTORY
touch git-hash.txt
touch git-tag.txt
echo $GIT-$DATE > git-hash.txt
echo $GIT_SHORT_TAG > git-tag.txt




echo =======================================================
echo Setting up Nethermind packages completed
echo =======================================================