#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

export GPG_TTY=$(tty)

set -e

echo "Publishing packages to Downloads page"

cd $GITHUB_WORKSPACE/$PACKAGE_DIR

for rid in "linux-x64" "linux-arm64" "windows-x64" "macos-x64" "macos-arm64"
do
  file_name=$(basename *$rid*)

  echo "Signing $file_name"

  gpg --batch --detach-sign --passphrase=$PASS --pinentry-mode loopback --armor $file_name

  echo "Uploading $file_name"

  curl https://downloads.nethermind.io/files?apikey=$DOWNLOADS_PAGE \
    -X POST \
    --fail-with-body \
    -# \
    -F "files=@$PWD/$file_name" \
    -F "files=@$PWD/$file_name.asc"
done

echo "Publishing completed"
