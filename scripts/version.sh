#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Prints the version an MSBuild project file declares: <VersionPrefix>, with -<VersionSuffix>
# appended when that property is present and not empty. This is the version that goes into the
# OCI version label of the images we publish.
#
# release.yml reads the same two properties with xmlstarlet and is deliberately left alone: its
# value also names the Git tag and the GitHub release, so rewiring it belongs in its own change.
# Both agree on every shape of the file that exists today.
#
# Usage:
#   scripts/version.sh [FILE]   # default: src/Nethermind/Directory.Build.props

set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
file=${1:-$repo_root/src/Nethermind/Directory.Build.props}

if [[ ! -f "$file" ]]; then
  echo "error: '$file' does not exist" >&2
  exit 1
fi

version_prefix=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$file")
version_suffix=$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$file")

if [[ -z "$version_prefix" ]]; then
  echo "error: '$file' declares no <VersionPrefix>" >&2
  exit 1
fi

version=$([[ -n "$version_suffix" ]] && echo "$version_prefix-$version_suffix" || echo "$version_prefix")

# A property declared twice makes sed print two lines, and callers interpolate this into a Docker
# build arg or a command they eval. The pattern is the one release-bootnode.yml validates its tag
# with: a single line that is safe as a Docker tag.
if [[ ! "$version" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
  echo "error: '$file' produced an unusable version: $version" >&2
  exit 1
fi

echo "$version"
