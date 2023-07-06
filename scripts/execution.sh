#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

sudo chown -R $(whoami) /usr/share/nethermind
opts=$@
if [ ${#opts} -gt 0 ]
then
  echo 'Executing Nethermind'
  sudo /usr/share/nethermind/nethermind $@
else
  echo 'Executing Nethermind Launcher'
  cd /usr/share/nethermind
  sudo /usr/share/nethermind/Nethermind.Launcher
fi
