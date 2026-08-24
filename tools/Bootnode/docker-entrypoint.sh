#!/bin/sh
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -e

has_option() {
  option_name=$1
  shift

  for argument do
    case "$argument" in
      "$option_name"|"$option_name"=*) return 0 ;;
    esac
  done

  return 1
}

if ! has_option "--data-dir" "$@"; then
  set -- --data-dir /nethermind-bootnode/data "$@"
fi

if ! has_option "--local-ip" "$@"; then
  set -- --local-ip 0.0.0.0 "$@"
fi

if ! has_option "--http-host" "$@"; then
  set -- --http-host 0.0.0.0 "$@"
fi

if ! has_option "--metrics-host" "$@"; then
  set -- --metrics-host 0.0.0.0 "$@"
fi

exec ./Nethermind.Bootnode "$@"
