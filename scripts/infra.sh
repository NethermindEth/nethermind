#!/bin/bash
# SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

if [ "$1" != "" ]; then
    CONFIG=$(echo "$1" | tr '[:upper:]' '[:lower:]')
else
    CONFIG="mainnet"
fi

cp ~/nethermind_$CONFIG/keystore/node.key.plain $CONFIG.key
rm -r ~/nethermind_$CONFIG
cp -r ~/nethermind ~/nethermind_$CONFIG
cd ~/nethermind_$CONFIG
cp ~/NLog.$CONFIG.config ~/nethermind_$CONFIG/NLog.config
mkdir ~/nethermind_$CONFIG/keystore
cp ~/$CONFIG.key ~/nethermind_$CONFIG/keystore/node.key.plain
DB_PATH="/root/db/$CONFIG"
echo "DB PATH: " $DB_PATH
cat ~/$CONFIG.cfg | jq '.Init.BaseDbPath = "'$DB_PATH'"' | sponge ~/$CONFIG.cfg
dotnet Nethermind.Runner.dll --config ../$CONFIG.cfg
