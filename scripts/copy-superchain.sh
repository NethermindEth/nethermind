#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

cp output/chainspec/* ../src/Nethermind/Chains
cp output/runner/* ../src/Nethermind/Nethermind.Runner/configs
