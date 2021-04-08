#!/bin/bash
export GPG_TTY=$(tty)
#exit when any command fails
set -e
echo =======================================================
echo Uploading files to Downloads page
echo =======================================================

cd $RELEASE_DIRECTORY

cd $LIN_RELEASE && LIN_FILE="$(basename nethermind-linux-amd64-*)" && cd ..
cd $OSX_RELEASE && OSX_FILE="$(basename nethermind-darwin-amd64-*)" && cd ..
cd $WIN_RELEASE && WIN_FILE="$(basename nethermind-windows-amd64-*)" && cd ..
cd $LIN_ARM64_RELEASE && LIN_ARM64_FILE="$(basename nethermind-linux-arm64-*)" && cd ..

echo =======================================================
echo Signing files with gpg
echo =======================================================

cd $LIN_RELEASE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $LIN_FILE
cd ..
cd $WIN_RELEASE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $WIN_FILE
cd ..
cd $OSX_RELEASE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $OSX_FILE
cd ..
cd $LIN_ARM64_RELEASE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $LIN_ARM64_FILE
cd ..

echo =======================================================
echo Files have been successfully signed
echo =======================================================

cd $LIN_RELEASE
filename_lin=${LIN_FILE::-13}
extension=${LIN_FILE##*.}

mv $LIN_FILE $filename_lin.$extension
mv $LIN_FILE.asc $filename_lin.$extension.asc
curl -# -F "files=@${PWD}/${filename_lin}.${extension}" -F "files=@${PWD}/${filename_lin}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $WIN_RELEASE
filename_win=${WIN_FILE::-13}

mv $WIN_FILE $filename_win.$extension
mv $WIN_FILE.asc $filename_win.$extension.asc

curl -# -F "files=@${PWD}/${filename_win}.${extension}" -F "files=@${PWD}/${filename_win}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $OSX_RELEASE
filename_osx=${OSX_FILE::-13}

mv $OSX_FILE $filename_osx.$extension
mv $OSX_FILE.asc $filename_osx.$extension.asc

curl -# -F "files=@${PWD}/${filename_osx}.${extension}" -F "files=@${PWD}/${filename_osx}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $LIN_ARM64_RELEASE
filename_lin_arm64=${LIN_ARM64_FILE::-13}

mv $LIN_ARM64_FILE $filename_lin_arm64.$extension
mv $LIN_ARM64_FILE.asc $filename_lin_arm64.$extension.asc
curl -# -F "files=@${PWD}/${filename_lin_arm64}.${extension}" -F "files=@${PWD}/${filename_lin_arm64}.${extension}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

echo =======================================================
echo Files have been successfully uploaded to Downloads page
echo =======================================================