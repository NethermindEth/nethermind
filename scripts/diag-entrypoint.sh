#!/bin/bash
# SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

./nethermind "$@" 2>&1 &

pid=$(pidof ./nethermind)

#dotnet-trace collect -p $pid -o /nethermind/diag/dotnet.nettrace
dottrace attach $pid --save-to=/nethermind/diag/dottrace --service-output=on --profiling-type=timeline
