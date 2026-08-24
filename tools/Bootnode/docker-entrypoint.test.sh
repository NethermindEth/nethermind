#!/bin/sh
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

set -eu

test_dir=$(mktemp -d)
trap 'rm -rf "$test_dir"' EXIT

cp "$(dirname "$0")/docker-entrypoint.sh" "$test_dir/docker-entrypoint.sh"
cat > "$test_dir/Nethermind.Bootnode" <<'EOF'
#!/bin/sh
printf '%s\n' "$@"
EOF
chmod +x "$test_dir/Nethermind.Bootnode"

assert_args() {
  name=$1
  expected=$2
  shift 2

  actual=$(cd "$test_dir" && sh ./docker-entrypoint.sh "$@")
  if [ "$actual" != "$expected" ]; then
    printf '%s\nExpected:\n%s\nActual:\n%s\n' "$name" "$expected" "$actual" >&2
    exit 1
  fi
}

defaults='--metrics-host
0.0.0.0
--http-host
0.0.0.0
--local-ip
0.0.0.0
--data-dir
/nethermind-bootnode/data'

assert_args "defaults" "$defaults"
assert_args "unrelated arguments" "$defaults
--protocols
discv5" --protocols discv5
assert_args "explicit values" '--data-dir
/custom
--local-ip
::
--http-host
127.0.0.1
--metrics-host
127.0.0.2' --data-dir /custom --local-ip :: --http-host 127.0.0.1 --metrics-host 127.0.0.2
assert_args "equals form" '--metrics-host
0.0.0.0
--http-host
0.0.0.0
--local-ip
0.0.0.0
--data-dir=/custom' --data-dir=/custom
