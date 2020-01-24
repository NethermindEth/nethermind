#!/bin/sh
set -e

chown nethermind:nethermind /data
cp -npr /nethermind/configs /nethermind/Data/static-nodes.json /data

exec gosu nethermind:nethermind /nethermind/Nethermind.Runner \
     --Init.BaseDbPath=/data/database \
	 --configsDirectory=/data/configs \
	 --KeyStore.KeyStoreDirectory=/data/keystore \
	 --Init.LogDirectory=/data/logs \
	 --Init.StaticNodesPath=/data/static-nodes.json \
	 "$@"