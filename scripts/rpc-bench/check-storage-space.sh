#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Check the filesystems that the ARM RPC benchmark can consume before and after a run. This helper
# deliberately has no reclaim or mutation path; the runner must fail closed when a path or reading
# is unavailable instead of guessing from a rounded gigabyte value.
set -uo pipefail

ARCH="${ARCH:-}"
SCRATCH_ROOT="${SCRATCH_ROOT:-}"
ROOT_PATH="${ROOT_PATH:-/}"                    # test seam; production uses the host root
CONTAINERD_PATH="${CONTAINERD_PATH:-/var/lib/containerd}"  # test seam; production default
RUNNER_TEMP="${RUNNER_TEMP:-}"
DF_COMMAND="${DF_COMMAND:-df}"              # test seam; production uses the host df command
PHASE="${1:-check}"

SMALL_MIN_BYTES=$((1024 * 1024 * 1024))
HEAVY_MIN_BYTES=$((6 * 1024 * 1024 * 1024))
failed=0

error() {
  echo "::error::$*" >&2
  failed=1
}

nearest_existing() {
  local candidate="$1"
  while [[ ! -e "${candidate}" && "${candidate}" != "/" ]]; do
    candidate="${candidate%/*}"
    [[ -n "${candidate}" ]] || candidate="/"
  done
  readlink -e -- "${candidate}" 2>/dev/null || true
}

available_bytes() {
  local path="$1" output value
  if ! output="$("${DF_COMMAND}" -B1 --output=avail -- "${path}" 2>/dev/null)"; then
    return 1
  fi
  value="$(printf '%s\n' "${output}" | awk '$1 ~ /^[0-9]+$/ { value=$1 } END { print value }')"
  [[ "${value}" =~ ^[0-9]+$ ]] || return 1
  printf '%s\n' "${value}"
}

check_path() {
  local label="$1" requested="$2" minimum="$3" allow_missing="$4"
  local resolved available
  if [[ -z "${requested}" || "${requested}" != /* ]]; then
    error "${label}: path is missing or not absolute"
    return 0
  fi

  if [[ -e "${requested}" ]]; then
    resolved="$(readlink -e -- "${requested}" 2>/dev/null || true)"
  elif [[ "${allow_missing}" == "true" ]]; then
    resolved="$(nearest_existing "${requested}")"
    echo "${label}: requested path is not created; checking nearest existing ancestor ${resolved:-<none>}"
  else
    error "${label}: path '${requested}' is missing"
    return 0
  fi
  if [[ -z "${resolved}" || "${resolved}" != /* ]]; then
    error "${label}: canonical path is unavailable"
    return 0
  fi

  if ! available="$(available_bytes "${resolved}")"; then
    error "${label}: df did not return numeric available bytes for '${resolved}'"
    return 0
  fi
  echo "${PHASE}: ${label} path=${resolved} available_bytes=${available} required_bytes=${minimum}"
  if (( available < minimum )); then
    error "${label}: only ${available} bytes available; need at least ${minimum}"
  fi
}

if [[ "${ARCH}" != "arm64" ]]; then
  error "storage check is only supported for ARCH=arm64, got '${ARCH:-<unset>}'"
else
  echo "Storage check phase=${PHASE} arch=${ARCH}"
  # Keep 1 GiB on the runner filesystems for logs and sanitized metadata while bulk storage uses
  # the stricter 6 GiB reserve needed for images, scratch overlays, and raw corpus data.
  check_path "root filesystem" "${ROOT_PATH}" "${SMALL_MIN_BYTES}" false
  check_path "RUNNER_TEMP" "${RUNNER_TEMP}" "${SMALL_MIN_BYTES}" false
  check_path "containerd storage" "${CONTAINERD_PATH}" "${HEAVY_MIN_BYTES}" false

  if ! docker_root="$(docker info --format '{{.DockerRootDir}}' 2>/dev/null)"; then
    error "DockerRootDir is missing or invalid"
  elif [[ -z "${docker_root}" || "${docker_root}" == "<no value>" || "${docker_root}" != /* || "${docker_root}" == *$'\n'* ]]; then
    error "DockerRootDir is missing or invalid"
  else
    check_path "DockerRootDir" "${docker_root}" "${HEAVY_MIN_BYTES}" false
  fi
  check_path "configured scratch" "${SCRATCH_ROOT}" "${HEAVY_MIN_BYTES}" true
fi

exit "${failed}"
