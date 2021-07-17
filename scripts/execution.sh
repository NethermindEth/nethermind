#!/bin/bash
sudo chown -R $(whoami) /usr/share/nethermind
sudo /usr/share/nethermind/Nethermind.Runner $@
