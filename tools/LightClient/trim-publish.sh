#!/bin/sh
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -eu
publish=${1:?Pass the light-client publish directory}
test -f "$publish/Nethermind.LightClient.dll"

# VerifiedCall uses native EVM tracers; the LC exposes no JavaScript debug RPCs.
# Keep the managed ClearScript assemblies for transitive assembly metadata references.
find "$publish" -type f -name 'ClearScriptV8.*' -delete
rm -f -- "$publish/ClearScript.V8.ICUData.dll" "$publish"/Nethermind.JsonRpc.SourceGenerator.Build.*
rm -rf -- "$publish/Data/JSTracers"
