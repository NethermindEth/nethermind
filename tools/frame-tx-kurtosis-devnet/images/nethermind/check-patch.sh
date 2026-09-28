#!/usr/bin/env bash
# Applies patch.sh to a scratch export of this checkout's HEAD and compiles the projects it
# touches, so an upstream move shows up in a couple of minutes rather than halfway through an
# image build. The working tree is never modified.
#
#   images/nethermind/check-patch.sh [max-verify-gas]    # default 500000
#
# Needs the .NET SDK from global.json.
set -euo pipefail

readonly CEILING="${1:-500000}"
readonly HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(git -C "${HERE}" rev-parse --show-toplevel)"

scratch=$(mktemp -d "${TMPDIR:-/tmp}/frame-tx-patch-check.XXXXXX")
trap 'rm -rf "${scratch}"' EXIT

git -C "${REPO_ROOT}" archive HEAD src/Nethermind global.json nuget.config \
  Directory.Build.props Directory.Build.targets Directory.Packages.props | tar -x -C "${scratch}"

bash "${HERE}/patch.sh" "${scratch}/src/Nethermind" "${CEILING}"

dotnet build "${scratch}/src/Nethermind/Nethermind.Specs/Nethermind.Specs.csproj" -c release -v quiet -nologo

echo "patch.sh applies to HEAD and Nethermind.Specs compiles at MaxVerifyGas=${CEILING}"
