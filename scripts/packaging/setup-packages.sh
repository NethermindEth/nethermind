RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

echo =======================================================
echo Setting up Nethermind packages
echo =======================================================

rm -rf $RELEASE_DIRECTORY
mkdir $RELEASE_DIRECTORY
cd $RELEASE_DIRECTORY
mkdir $LIN_RELEASE
mkdir $OSX_RELEASE
mkdir $WIN_RELEASE

echo =======================================================
echo Setting up Nethermind packages completed
echo =======================================================