#!/usr/bin/env bash

set -euo pipefail

cp output/chainspec/* ../src/Nethermind/Chains
cp output/runner/* ../src/Nethermind/Nethermind.Runner/configs
