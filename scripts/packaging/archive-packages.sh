RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

echo =======================================================
echo Archiving Nethermind packages
echo =======================================================

cd $RELEASE_DIRECTORY/$LIN_RELEASE && zip -r $LIN_RELEASE.zip . && cd ../../
cd $RELEASE_DIRECTORY/$OSX_RELEASE && zip -r $OSX_RELEASE.zip . && cd ../../
cd $RELEASE_DIRECTORY/$WIN_RELEASE && zip -r $WIN_RELEASE.zip . && cd ../../

echo =======================================================
echo Archiving Nethermind packages completed
echo =======================================================