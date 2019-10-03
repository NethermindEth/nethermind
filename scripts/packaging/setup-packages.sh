RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

echo =======================================================
echo Setting up Nethermind packages
echo =======================================================

DATE=`date +%Y%m%d`

rm -rf $RELEASE_DIRECTORY
mkdir $RELEASE_DIRECTORY
cd $RELEASE_DIRECTORY
mkdir $LIN_RELEASE
mkdir $OSX_RELEASE
mkdir $WIN_RELEASE

echo =======================================================
echo Creating  git-hash and git-tag files
echo =======================================================
cd ..
cd ~/repo_pub/nethermind
GIT="$(git rev-parse --short=7 HEAD)"
GIT_TAG="$(git describe --tags)"
GIT_SHORT_TAG="$(echo $GIT_TAG | cut -d- -f1)"
cd ~/repo_pub/$RELEASE_DIRECTORY
touch git-hash.txt
touch git-tag.txt
echo $GIT-$DATE> git-hash.txt
echo $GIT_SHORT_TAG > git-tag.txt

echo =======================================================
echo Success, git-hash and git-tag  files have been created
echo =======================================================

echo =======================================================
echo Setting up Nethermind packages completed
echo =======================================================

