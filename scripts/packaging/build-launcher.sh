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
/usr/bin/nexe -t linux-x64-10.0.0 -o $APP_NAME && mv $APP_NAME ../$RELEASE_DIRECTORY/$LIN_RELEASE
/usr/bin/nexe -t darwin-x64-10.0.0 -o $APP_NAME && mv $APP_NAME ../$RELEASE_DIRECTORY/$OSX_RELEASE
/usr/bin/nexe -t windows-x64-10.0.0 -o $APP_NAME.exe && mv $APP_NAME.exe ../$RELEASE_DIRECTORY/$WIN_RELEASE

echo =======================================================
echo Building Nethermind Launcher completed
echo =======================================================
