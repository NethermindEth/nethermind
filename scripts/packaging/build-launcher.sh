RELEASE_DIRECTORY=nethermind-packages
LAUNCHER_PATH=nethermind.launcher
APP_NAME=Nethermind.Launcher
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64

echo =======================================================
echo Building Nethermind Launcher
echo =======================================================

cd $LAUNCHER_PATH
npm i
pkg index.js -t node12-linux -o $APP_NAME && mv $APP_NAME ../$RELEASE_DIRECTORY/$LIN_RELEASE
pkg index.js -t node12-osx -o $APP_NAME && mv $APP_NAME ../$RELEASE_DIRECTORY/$OSX_RELEASE
pkg index.js -t node12-win -o $APP_NAME.exe && mv $APP_NAME.exe ../$RELEASE_DIRECTORY/$WIN_RELEASE

echo =======================================================
echo Building Nethermind Launcher completed
echo =======================================================
