#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# apply: disable turbo boost, set the performance governor and cap scaling_max_freq (CPU_MAX_FREQ_KHZ, optional),
#        saving the original sysfs values under STATE_DIR (keep it outside RUNNER_TEMP so a killed run's state survives
#        job cleanup). restore: write them back. Best effort — a box without cpufreq sysfs (e.g. a cloud ARM VM) is
#        logged and skipped.

set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/rpc-bench/lib.sh
source "$HERE/lib.sh"

: "${STATE_DIR:?directory for the saved sysfs values}"
CPU_MAX_FREQ_KHZ="${CPU_MAX_FREQ_KHZ:-}"
CPU_SYSFS="${CPU_SYSFS:-/sys/devices/system/cpu}"   # overridable so the tests can drive a fake tree
SAVED="$STATE_DIR/cpu-sysfs.orig"

write_sys() { as_root sh -c "printf '%s' '$2' > '$1'" 2>/dev/null; }
save_sys() { printf '%s\t%s\n' "$1" "$(cat "$1")" >> "$SAVED"; }

turbo_path() {
  [[ -e "$CPU_SYSFS/intel_pstate/no_turbo" ]] && { echo "$CPU_SYSFS/intel_pstate/no_turbo 1"; return; }
  [[ -e "$CPU_SYSFS/cpufreq/boost" ]] && echo "$CPU_SYSFS/cpufreq/boost 0"
}

apply() {
  [[ -z "$CPU_MAX_FREQ_KHZ" || "$CPU_MAX_FREQ_KHZ" =~ ^[1-9][0-9]*$ ]] || die "CPU_MAX_FREQ_KHZ must be a positive integer, got '$CPU_MAX_FREQ_KHZ'"
  mkdir -p "$STATE_DIR"
  # A run killed between apply and restore leaves the box capped; restoring first keeps the cap from being
  # recorded as the "original" and made permanent.
  [[ -s "$SAVED" ]] && { log "::warning::stale saved cpu state from an earlier run — restoring it first"; restore; }
  # restore keeps the file when it could not write every value back, so truncating here would record the
  # still-capped values as the originals - the same failure it just prevented, one step along.
  if [[ -s "$SAVED" ]]; then
    log "::error::refusing to apply over unrestored cpu state in $SAVED — the box is still capped; restore it by hand first"
    return 1
  fi
  : > "$SAVED"
  local turbo off policy n policies=()
  read -r turbo off <<< "$(turbo_path)"
  # One entry per cpufreq policy, not per CPU: cpuN/cpufreq links to its policy and a cluster's CPUs share one, so a
  # per-CPU walk read the second CPU's "original" after the first one's write. Every original is recorded before the
  # first write, turbo's included, so none of them is a value this script already changed.
  for policy in "$CPU_SYSFS"/cpufreq/policy[0-9]*; do [[ -e "$policy/scaling_governor" ]] && policies+=("$policy"); done
  [[ -z "${turbo:-}" ]] || save_sys "$turbo"
  for policy in "${policies[@]}"; do
    save_sys "$policy/scaling_governor"
    [[ -z "$CPU_MAX_FREQ_KHZ" ]] || save_sys "$policy/scaling_max_freq"
  done

  if [[ -n "${turbo:-}" ]]; then
    write_sys "$turbo" "$off" && log "turbo boost disabled ($turbo=$off)" || log "::warning::could not write $turbo"
  fi
  if (( ${#policies[@]} == 0 )); then
    log "no cpufreq sysfs on this host — CPU frequency left as is"
    [[ -z "$CPU_MAX_FREQ_KHZ" ]] || log "::warning::CPU_MAX_FREQ_KHZ set but no scaling_max_freq sysfs on this host"
    return 0
  fi
  n=0
  for policy in "${policies[@]}"; do write_sys "$policy/scaling_governor" performance && n=$((n + 1)); done
  log "governor=performance on $n of ${#policies[@]} cpufreq policies"
  if [[ -n "$CPU_MAX_FREQ_KHZ" ]]; then
    n=0
    for policy in "${policies[@]}"; do write_sys "$policy/scaling_max_freq" "$CPU_MAX_FREQ_KHZ" && n=$((n + 1)); done
    log "scaling_max_freq=${CPU_MAX_FREQ_KHZ} kHz on $n of ${#policies[@]} cpufreq policies (now $(cat "${policies[0]}/scaling_max_freq"))"
  fi
}

restore() {
  [[ -s "$SAVED" ]] || { log "nothing to restore"; return 0; }
  local path value n=0 total=0
  while IFS=$'\t' read -r path value; do
    [[ -n "$path" && -n "$value" ]] || continue
    total=$((total + 1))
    write_sys "$path" "$value" && n=$((n + 1))
  done < "$SAVED"
  log "restored $n of $total cpu sysfs value(s)"
  # write_sys swallows its errors, so only drop the record once every value is actually back. Removing it
  # after a restore that wrote nothing leaves the box capped with no original to return to, and the next
  # apply would record the cap as the original - on a box shared with expb.
  if [[ "$n" -eq "$total" ]]; then
    rm -f "$SAVED"
  else
    log "::warning::keeping $SAVED - $((total - n)) value(s) could not be restored"
  fi
}

case "${1:-}" in
  apply) apply ;;
  restore) restore ;;
  *) die "usage: $0 apply|restore" ;;
esac
