#!/bin/sh
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

if [ "$#" -eq 0 ]; then
  exec ./Nethermind.Bootnode \
    --data-dir /nethermind-bootnode/data \
    --local-ip 0.0.0.0 \
    --http-host 0.0.0.0 \
    --metrics-host 0.0.0.0
fi

exec ./Nethermind.Bootnode "$@"
