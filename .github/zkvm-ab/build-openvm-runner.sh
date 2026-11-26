#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Builds the OpenVM runner of a bflat-openvm checkout with openvm-cost-main.rs as its entry point
# and saves the image as <out>.
# Usage: build-openvm-runner.sh <bflat-openvm checkout> <out>
set -euo pipefail

src=$1
cp "$(dirname "$0")/openvm-cost-main.rs" "$src/runner/src/main.rs"
sed -i 's/^RUN cargo test --release -p openvm-runelf \\$/RUN true \\/' "$src/runner/Dockerfile"
grep -q '^RUN true \\$' "$src/runner/Dockerfile" || { echo "::error::the runner Dockerfile changed; update the test step patch" >&2; exit 1; }
docker build -t zkab-openvm-cost:local "$src/runner"
docker save zkab-openvm-cost:local | gzip -1 > "$2"
