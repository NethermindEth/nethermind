#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

sudo chown -R $(whoami) /usr/share/nethermind
opts=$@
if [ ${#opts} -gt 0 ]
then
  echo 'Executing Nethermind Runner'
  sudo /usr/share/nethermind/Nethermind.Runner $@
else
  echo 'Executing Nethermind Launcher'
  cd /usr/share/nethermind
  sudo /usr/share/nethermind/Nethermind.Launcher
fi
