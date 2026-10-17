#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
rid=${1:?Usage: native.sh <runtime-identifier>}
case "$rid" in
  linux-x64) target=x86_64-unknown-linux-gnu; library=libnethermind_patricia.so ;;
  linux-arm64) target=aarch64-unknown-linux-gnu; library=libnethermind_patricia.so ;;
  osx-x64) target=x86_64-apple-darwin; library=libnethermind_patricia.dylib ;;
  osx-arm64) target=aarch64-apple-darwin; library=libnethermind_patricia.dylib ;;
  win-x64) target=x86_64-pc-windows-msvc; library=nethermind_patricia.dll ;;
  win-arm64) target=aarch64-pc-windows-msvc; library=nethermind_patricia.dll ;;
  *) echo "Unsupported native Patricia RID: $rid" >&2; exit 1 ;;
esac

crate="$root/src/Nethermind/Nethermind.Trie.Native/rust"
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-"$crate/target"}
# A failed build must never leave a previously staged artifact usable by publish.
output="$crate/target/native/$rid"
mkdir -p "$output"
rm -f "$output/$library"
cargo build --manifest-path "$crate/Cargo.toml" --release --locked --target "$target"
cp "$CARGO_TARGET_DIR/$target/release/$library" "$output/$library"
