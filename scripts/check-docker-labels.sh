#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Checks that the images we publish carry Nethermind's OCI labels rather than the ones they would
# otherwise inherit from the .NET base image (which in turn inherits Ubuntu's).
#
# The labels fed by a build arg are the fragile ones: an ARG is scoped to the stage that declares
# it, so a final stage that does not redeclare COMMIT_HASH, VERSION and BUILD_TIMESTAMP still
# builds, but resolves those labels to an empty string. Only inspecting a built image catches it.
#
# The version, revision and created labels are checked self-referentially: this script supplies
# the build args and asserts the same values came back. That proves each Dockerfile wires the args
# into its labels; it does not prove the publishing workflows pass them.
#
# By default only the final stage is built: the `build` stage is replaced by a stub context, which
# takes seconds instead of a full .NET compile and is enough to check the labels. Pass --full to
# build the image the way the release workflow does and check that instead.
#
# Requires Docker with buildx, jq, and GNU date and find (this uses `date -d` and `find -printf`).
#
# Usage:
#   scripts/check-docker-labels.sh [--full] [--dockerfile PATH]...

set -euo pipefail

repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
readonly repo_root

usage() {
  cat << 'EOF'
usage: check-docker-labels.sh [--full] [--dockerfile PATH]...

  --full             Build the whole image instead of only its final stage.
  --dockerfile PATH  Check this Dockerfile (repeatable). Defaults to every published image.

Requires Docker with buildx, jq, and GNU date and find.
EOF
}

full=0
dockerfiles=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --full)
      full=1
      shift
      ;;
    --dockerfile)
      [[ $# -ge 2 ]] || { echo "error: --dockerfile needs a path" >&2; exit 2; }
      dockerfiles+=("$2")
      shift 2
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "error: unknown argument '$1'" >&2
      usage >&2
      exit 2
      ;;
  esac
done

# Every Dockerfile in the repository is checked except the ones listed here, so a new published
# image is checked by default and has to be excluded on purpose. Anything discovered that has no
# entry in the expectations table below fails the run rather than being skipped.
not_published=(
  Dockerfile.diag                                 # ad hoc dotTrace image for gas benchmarks
  Dockerfile.pgo                                  # collect-pgo-profile.yml, throwaway pgo-<run id> tag
  scripts/build/Dockerfile                        # builds the release packages, the image is not pushed
  scripts/build/deb/Dockerfile                    # builds the .deb, the image is not pushed
  src/Nethermind/Nethermind.Runner/Dockerfile     # Visual Studio's debug container
  src/Nethermind/Nethermind.Test.Runner/Dockerfile
  tools/EngineApiProxy/Dockerfile                 # built in run-e2e-tests.yml, never pushed
  tools/Kute/Dockerfile
  tools/RpcTests/RpcTests.Monitor/Dockerfile      # pushed as nethermindeth/rpc-monitor, internal ops tooling
  tools/SendBlobs/Dockerfile
)

if [[ ${#dockerfiles[@]} -eq 0 ]]; then
  mapfile -t discovered < <(
    cd "$repo_root" &&
      find . -type f \( -name Dockerfile -o -name 'Dockerfile.*' \) -not -path './.git/*' -printf '%P\n' |
      sort
  )

  for candidate in "${discovered[@]}"; do
    excluded=0

    for skipped in "${not_published[@]}"; do
      if [[ "$candidate" == "$skipped" ]]; then
        excluded=1
        break
      fi
    done

    if [[ $excluded -eq 0 ]]; then
      dockerfiles+=("$candidate")
    fi
  done
fi

# The same values the release workflows pass, so what is checked here is what gets published.
commit_hash=$(git -C "$repo_root" rev-parse HEAD)
source_date_epoch=$(git -C "$repo_root" log -1 --format=%ct)
build_timestamp=$(date -u -d "@$source_date_epoch" +%Y-%m-%dT%H:%M:%SZ)

if [[ -z "$commit_hash" || -z "$build_timestamp" ]]; then
  echo "error: could not determine the commit and timestamp to build with" >&2
  exit 1
fi

# A stand-in for the build stage's /publish, so the final stage's COPY --from=build resolves
# without compiling anything.
stub_context=$(mktemp -d)
trap 'rm -rf "$stub_context"' EXIT
mkdir "$stub_context/publish"
: > "$stub_context/publish/nethermind"

failures=0
labels=''

check_label() {
  local key=$1 want=$2 got
  got=$(jq -r --arg k "$key" '.[$k] // ""' <<< "$labels")

  if [[ "$got" != "$want" ]]; then
    printf '  FAIL %s\n         expected: %s\n         actual:   %s\n' \
      "$key" "$want" "${got:-<empty or missing>}" >&2
    failures=$((failures + 1))
    return
  fi

  printf '  ok   %s = %s\n' "$key" "$got"
}

for dockerfile in "${dockerfiles[@]}"; do
  case "$dockerfile" in
    tools/Bootnode/Dockerfile)
      title='Nethermind Bootnode'
      description='Standalone discovery bootnode for discv4 and discv5.'
      documentation='https://github.com/NethermindEth/nethermind/blob/master/tools/Bootnode/README.md'
      # release-bootnode.yml versions the Bootnode from its own project file, not the client's.
      version_file=tools/Bootnode/Nethermind.Bootnode/Nethermind.Bootnode.csproj
      ;;
    Dockerfile | Dockerfile.chiseled)
      title='Nethermind'
      description='A robust execution client for Ethereum node operators.'
      documentation='https://docs.nethermind.io'
      version_file=src/Nethermind/Directory.Build.props
      ;;
    *)
      echo "error: '$dockerfile' has no expected labels here. Add them, or add it to" >&2
      echo "       not_published in $(basename "${BASH_SOURCE[0]}") if it is not released." >&2
      exit 2
      ;;
  esac

  version=$("$repo_root/scripts/version.sh" "$repo_root/$version_file")

  tag="nethermind-label-check:$(echo "$dockerfile" | tr '/.' '--')"
  build=(docker buildx build "$repo_root" -f "$repo_root/$dockerfile" -t "$tag"
    --build-arg COMMIT_HASH="$commit_hash"
    --build-arg VERSION="$version"
    --build-arg BUILD_TIMESTAMP="$build_timestamp"
    --load)

  if [[ $full -eq 1 ]]; then
    build+=(--build-arg SOURCE_DATE_EPOCH="$source_date_epoch")
  else
    build+=(--build-context "build=$stub_context")
  fi

  echo "Building $dockerfile as $tag"
  "${build[@]}"

  labels=$(docker image inspect --format '{{json .Config.Labels}}' "$tag")

  echo "Checking the labels of $tag"
  check_label org.opencontainers.image.title "$title"
  check_label org.opencontainers.image.description "$description"
  check_label org.opencontainers.image.vendor 'Demerzel Solutions Limited'
  check_label org.opencontainers.image.licenses 'LGPL-3.0-only'
  check_label org.opencontainers.image.url 'https://nethermind.io/nethermind-client'
  check_label org.opencontainers.image.documentation "$documentation"
  check_label org.opencontainers.image.source 'https://github.com/NethermindEth/nethermind'
  check_label org.opencontainers.image.version "$version"
  check_label org.opencontainers.image.revision "$commit_hash"
  # build_timestamp is RFC 3339 by construction, so matching it exactly is also the format check.
  check_label org.opencontainers.image.created "$build_timestamp"
done

if [[ $failures -gt 0 ]]; then
  echo "$failures label check(s) failed" >&2
  exit 1
fi

echo "All image labels are correct"
