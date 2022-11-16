#!/bin/bash
export GPG_TTY=$(tty)
#exit when any command fails
set -e

echo =======================================================
echo Uploading files to Downloads page
echo =======================================================

cd $RELEASE_PATH/$PACKAGE_DIR

LINUX_X64_FILE=$(basename *linux-x64*)
LINUX_ARM64_FILE=$(basename *linux-arm64*)
WIN_X64_FILE=$(basename *win-x64*)
OSX_X64_FILE=$(basename *osx-x64*)
OSX_ARM64_FILE=$(basename *osx-arm64*)

echo =======================================================
echo Signing files with gpg
echo =======================================================

gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $LINUX_X64_FILE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $LINUX_ARM64_FILE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $WIN_X64_FILE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $OSX_X64_FILE
gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $OSX_ARM64_FILE

echo =======================================================
echo Files have been successfully signed
echo =======================================================

curl --fail -# -F "files=@${PWD}/${LINUX_X64_FILE}" -F "files=@${PWD}/${LINUX_X64_FILE}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
curl --fail -# -F "files=@${PWD}/${LINUX_ARM64_FILE}" -F "files=@${PWD}/${LINUX_ARM64_FILE}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
curl --fail -# -F "files=@${PWD}/${WIN_X64_FILE}" -F "files=@${PWD}/${WIN_X64_FILE}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
curl --fail -# -F "files=@${PWD}/${OSX_X64_FILE}" -F "files=@${PWD}/${OSX_X64_FILE}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
curl --fail -# -F "files=@${PWD}/${OSX_ARM64_FILE}" -F "files=@${PWD}/${OSX_ARM64_FILE}.asc" https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE

echo =======================================================
echo Files have been successfully uploaded to Downloads page
echo =======================================================
