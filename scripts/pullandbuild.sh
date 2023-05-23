#!/bin/bash
# SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

cd ~/src/nethermind
git stash
git pull
git submodule update
git stash apply
cd ../..
cd ~/src/nethermind/src/Nethermind/Nethermind.Runner
dotnet build -c Release -o ~/nethermind
cp ~/NLog.config ~/nethermind/NLog.config
