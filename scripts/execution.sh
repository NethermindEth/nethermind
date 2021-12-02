#!/bin/bash
sudo chown -R $(whoami) /usr/share/nethermind
opts=$@
if [ ${#opts} -gt 0 ]
then
  echo 'Executing Nethermind Runner'
  sudo /usr/share/nethermind/Nethermind.Runner $@
else
  echo 'Executing Nethermind Launcher'
  sudo /usr/share/nethermind/Nethermind.Launcher
fi
