#!/bin/bash
export GPG_TTY=$(tty)
RELEASE_DIRECTORY=nethermind-packages
LIN_RELEASE=nethermind-lin-x64
OSX_RELEASE=nethermind-osx-x64
WIN_RELEASE=nethermind-win-x64
GH_OWNER=NethermindEth
GH_REPO=nethermind
PUB_RELEASE_DIRECTORY=~/repo_pub/nethermind-packages
DOWNLOADS_PAGE=$(az keyvault secret show --id https://neth-dev-keyvault.vault.azure.net/secrets/nethermind-packager-upload-api-key --vault-name neth-dev-keyvault --query value | tr -d '"')

echo =======================================================
echo Uploading files to Downloads page
echo =======================================================

cd $PUB_RELEASE_DIRECTORY

cd $LIN_RELEASE && LIN_FILE="$(basename nethermind-linux-amd64-*)" && cd ..
cd $OSX_RELEASE && OSX_FILE="$(basename nethermind-darwin-amd64-*)" && cd ..
cd $WIN_RELEASE && WIN_FILE="$(basename nethermind-windows-amd64-*)" && cd ..

echo =======================================================
echo Signing files with gpg
echo =======================================================

cd $LIN_RELEASE
echo $PASS | gpg --batch --detach-sign --passphrase-fd 0 --armor $LIN_FILE
cd ..
cd $WIN_RELEASE
echo $PASS | gpg --batch --detach-sign --passphrase-fd 0 --armor $WIN_FILE
cd ..
cd $OSX_RELEASE
echo $PASS | gpg --batch --detach-sign --passphrase-fd 0 --armor $OSX_FILE
cd ..

echo =======================================================
echo Files have been successfully signed 
echo =======================================================

cd $LIN_RELEASE
filename_lin=${LIN_FILE::-13} 
extension=${LIN_FILE##*.}

mv $LIN_FILE $filename_lin.$extension
mv $LIN_FILE.asc $filename_lin.$extension.asc
curl -# -F "files=@/root/repo_pub/nethermind-packages/nethermind-lin-x64/${filename_lin}.${extension}" -F "files=@/root/repo_pub/nethermind-packages/nethermind-lin-x64/${filename_lin}.${extension}.asc" http://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $WIN_RELEASE
filename_win=${WIN_FILE::-13} 

mv $WIN_FILE $filename_win.$extension
mv $WIN_FILE.asc $filename_win.$extension.asc

curl -# -F "files=@/root/repo_pub/nethermind-packages/nethermind-win-x64/${filename_win}.${extension}" -F "files=@/root/repo_pub/nethermind-packages/nethermind-win-x64/${filename_win}.${extension}.asc" http://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

cd $OSX_RELEASE
filename_osx=${OSX_FILE::-13}

mv $OSX_FILE $filename_osx.$extension
mv $OSX_FILE.asc $filename_osx.$extension.asc

curl -# -F "files=@/root/repo_pub/nethermind-packages/nethermind-osx-x64/${filename_osx}.${extension}" -F "files=@/root/repo_pub/nethermind-packages/nethermind-osx-x64/${filename_osx}.${extension}.asc" http://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE
cd ..

echo =======================================================
echo Files have been successfully uploaded to Downloads page
echo =======================================================

