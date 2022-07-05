#!/bin/bash
#exit when any command fails

set -e
echo $VERSION > ver
FIXED_VERSION=$(awk -F. '{ print $1"."$2$3$4"0"}' ver)
echo "nethermind ($FIXED_VERSION) bionic focal impish jammy; urgency=high

  * Nethermind client ($FIXED_VERSION release)

 -- Nethermind <devops@nethermind.io>  $( date -R )" > $RELEASE_DIRECTORY/nethermind/build/debian/changelog

cd nethermind/build/
debuild -S -uc -us
cd ..

echo 'Signing package'
debsign -p 'gpg --batch --yes --no-tty --pinentry-mode loopback --passphrase-file /home/runner/work/nethermind/PASSPHRASE' -S -k$PPA_GPG_KEYID nethermind_${FIXED_VERSION}_source.changes

echo 'Uploading'
dput -f ppa:nethermindeth/nethermind nethermind_${FIXED_VERSION}_source.changes
echo 'Publishing to PPA complete'