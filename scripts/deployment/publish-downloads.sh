#!/bin/bash
export GPG_TTY=$(tty)
#exit when any command fails
set -e

echo =======================================================
echo Uploading files to Downloads page
echo =======================================================

cd $RELEASE_PATH

cd $LINUX_X64_PKG && LINUX_X64_FILE="$(basename $LINUX_X64_PKG-*)" && cd ..
cd $LINUX_ARM64_PKG && LINUX_ARM64_FILE="$(basename $LINUX_ARM64_PKG-*)" && cd ..
cd $WIN_X64_PKG && WIN_X64_FILE="$(basename $WIN_X64_PKG-*)" && cd ..
cd $OSX_X64_PKG && OSX_X64_FILE="$(basename $OSX_X64_PKG-*)" && cd ..
cd $OSX_ARM64_PKG && OSX_ARM64_FILE="$(basename $OSX_ARM64_PKG-*)" && cd ..

echo =======================================================
echo Signing files with gpg
echo =======================================================

cd $LINUX_X64_PKG
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $LINUX_X64_FILE
cd ..
cd $LINUX_ARM64_PKG
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $LINUX_ARM64_FILE
cd ..
cd $WIN_X64_PKG
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $WIN_X64_FILE
cd ..
cd $OSX_X64_PKG
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $OSX_X64_FILE
cd ..
cd $OSX_ARM64_PKG
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $OSX_ARM64_FILE
cd ..

echo =======================================================
echo Files have been successfully signed
echo =======================================================

cd $LINUX_X64_PKG
filename_linux_x64=${LINUX_X64_FILE::-13}
extension=${LINUX_X64_FILE##*.}

mv $LINUX_X64_FILE filename_linux_x64.$extension
mv $LINUX_X64_FILE.asc filename_linux_x64.$extension.asc
curl --fail -# -F "files=@${PWD}/${filename_linux_x64}.${extension}" -F "files=@${PWD}/${filename_linux_x64}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $LINUX_ARM64_PKG
filename_linux_arm64=${LINUX_ARM64_FILE::-13}

mv $LINUX_ARM64_FILE $filename_linux_arm64.$extension
mv $LINUX_ARM64_FILE.asc $filename_linux_arm64.$extension.asc
curl --fail -# -F "files=@${PWD}/${filename_linux_arm64}.${extension}" -F "files=@${PWD}/${filename_linux_arm64}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $WIN_X64_PKG
filename_win_x64=${WIN_X64_FILE::-13}

mv $WIN_X64_FILE $filename_win_x64.$extension
mv $WIN_X64_FILE.asc $filename_win_x64.$extension.asc

curl --fail -# -F "files=@${PWD}/${filename_win_x64}.${extension}" -F "files=@${PWD}/${filename_win_x64}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $OSX_X64_PKG
filename_osx_x64=${OSX_X64_FILE::-13}

mv $OSX_X64_FILE $filename_osx_x64.$extension
mv $OSX_X64_FILE.asc $filename_osx_x64.$extension.asc

curl --fail -# -F "files=@${PWD}/${filename_osx_x64}.${extension}" -F "files=@${PWD}/${filename_osx_x64}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $OSX_ARM64_PKG
filename_osx_arm64=${OSX_ARM64_FILE::-13}

mv $OSX_ARM64_FILE $filename_osx_arm64.$extension
mv $OSX_ARM64_FILE.asc $filename_osx_arm64.$extension.asc
curl --fail -# -F "files=@${PWD}/${filename_osx_arm64}.${extension}" -F "files=@${PWD}/${filename_osx_arm64}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

echo =======================================================
echo Files have been successfully uploaded to Downloads page
echo =======================================================
