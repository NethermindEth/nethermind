#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

echo "Building package"

changelog="nethermind ($VERSION) jammy; urgency=high\n"
changelog+="  * Nethermind v$VERSION\n"
changelog+=" -- Nethermind <devops@nethermind.io>  $(date -R)"

cd $SCRIPTS_PATH
echo -e "$changelog" > debian/changelog

debuild -S -uc -us
cd ..

echo "Signing package"
debsign -p "gpg --batch --yes --no-tty --pinentry-mode loopback --passphrase-file $GITHUB_WORKSPACE/PASSPHRASE" \
  -S -k$PPA_GPG_KEYID nethermind_${VERSION}_source.changes

echo "Uploading package"
dput -f ppa:nethermindeth/nethermind nethermind_${VERSION}_source.changes
  
rm nethermind_$VERSION*

echo "Publishing completed"
