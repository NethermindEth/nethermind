#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

export GPG_TTY=$(tty)

set -e

echo "Publishing packages to Downloads page"

cd $GITHUB_WORKSPACE/$PACKAGE_DIR

for rid in "linux-x64" "linux-arm64" "windows-x64" "macos-x64" "macos-arm64"
do
  FILE_NAME=$(basename *$rid*)

  echo "Signing $FILE_NAME"

  gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $FILE_NAME

  echo "Uploading $FILE_NAME"

  curl https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE \
    -X POST \
    --fail-with-body \
    -# \
    -F "files=@$PWD/$FILE_NAME" \
    -F "files=@$PWD/$FILE_NAME.asc"
done

echo "Publishing completed"
