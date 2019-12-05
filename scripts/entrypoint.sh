#!/bin/sh
set -e

chown nethermind:nethermind /data
exec gosu nethermind:nethermind /nethermind/Nethermind.Runner --Init.BaseDbPath=/data "$@"