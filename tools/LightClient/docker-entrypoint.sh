#!/bin/sh
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

exec ./Nethermind.LightClient \
  --urls http://0.0.0.0:8545 \
  --data-dir /data \
  --beacon-port 9050 \
  --beacon-quic-port 9051 \
  --execution-port 30303 \
  "$@"
