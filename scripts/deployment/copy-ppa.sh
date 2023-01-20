#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

git clone https://git.launchpad.net/ubuntu-archive-tools
sudo apt-get install ubuntu-dev-tools -y --no-install-recommends

cd ubuntu-archive-tools

echo "Copying packages"

for release in "trusty" "bionic" "focal" "kinetic"
do
  python3 copy-package -y -b -p nethermindeth --ppa-name=nethermind -s jammy --to-suite=$release nethermind
done
