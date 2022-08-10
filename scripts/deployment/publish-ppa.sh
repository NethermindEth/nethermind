#!/bin/bash
#exit when any command fails
RELEASES="bionic focal impish jammy"

set -e
echo $VERSION > ver
FIXED_VERSION=$(awk -F. '{ print $1"."$2$3$4"0"}' ver)

cd $RELEASE_DIRECTORY/nethermind/
for release in ${RELEASES}; do

  echo "nethermind ($FIXED_VERSION) $release; urgency=high

  * Nethermind client ($FIXED_VERSION release)

 -- Nethermind <devops@nethermind.io>  $( date -R )" > $RELEASE_DIRECTORY/nethermind/build/debian/changelog
  cd build/
  debuild -S -uc -us
  cd ..
  echo 'Signing package'
  debsign -p 'gpg --batch --yes --no-tty --pinentry-mode loopback --passphrase-file /home/runner/work/nethermind/PASSPHRASE' -S -k$PPA_GPG_KEYID nethermind_${FIXED_VERSION}_source.changes
  echo 'Uploading'
  dput -f ppa:nethermindeth/nethermind nethermind_${FIXED_VERSION}_source.changes
  echo "Publishing $release release to PPA complete"
  echo 'Cleanup'
  rm nethermind_$FIXED_VERSION*
done