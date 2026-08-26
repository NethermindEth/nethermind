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
SAVED="$STATE_DIR/cpu-sysfs.orig"

write_sys() { as_root sh -c "printf '%s' '$2' > '$1'" 2>/dev/null; }

turbo_path() {
  [[ -e /sys/devices/system/cpu/intel_pstate/no_turbo ]] && { echo "/sys/devices/system/cpu/intel_pstate/no_turbo 1"; return; }
  [[ -e /sys/devices/system/cpu/cpufreq/boost ]] && echo "/sys/devices/system/cpu/cpufreq/boost 0"
}

apply() {
  mkdir -p "$STATE_DIR"
  # A run killed between apply and restore leaves the box capped; restoring first keeps the cap from being
  # recorded as the "original" and made permanent.
  [[ -s "$SAVED" ]] && { log "::warning::stale saved cpu state from an earlier run — restoring it first"; restore; }
  : > "$SAVED"
  local path off governors freqs
  read -r path off <<< "$(turbo_path)"
  if [[ -n "${path:-}" ]]; then
    printf '%s\t%s\n' "$path" "$(cat "$path")" >> "$SAVED"
    write_sys "$path" "$off" && log "turbo boost disabled ($path=$off)" || log "::warning::could not write $path"
  fi
  governors=(/sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_governor)
  if [[ -e "${governors[0]}" ]]; then
    for path in "${governors[@]}"; do printf '%s\t%s\n' "$path" "$(cat "$path")" >> "$SAVED"; write_sys "$path" performance; done
    log "governor=performance on ${#governors[@]} cpus"
  else
    log "no cpufreq sysfs on this host — CPU frequency left as is"
  fi
  if [[ -n "$CPU_MAX_FREQ_KHZ" ]]; then
    [[ "$CPU_MAX_FREQ_KHZ" =~ ^[1-9][0-9]*$ ]] || die "CPU_MAX_FREQ_KHZ must be a positive integer, got '$CPU_MAX_FREQ_KHZ'"
    freqs=(/sys/devices/system/cpu/cpu[0-9]*/cpufreq/scaling_max_freq)
    if [[ -e "${freqs[0]}" ]]; then
      for path in "${freqs[@]}"; do printf '%s\t%s\n' "$path" "$(cat "$path")" >> "$SAVED"; write_sys "$path" "$CPU_MAX_FREQ_KHZ"; done
      log "scaling_max_freq=${CPU_MAX_FREQ_KHZ} kHz on ${#freqs[@]} cpus (now $(cat "${freqs[0]}"))"
    else
      log "::warning::CPU_MAX_FREQ_KHZ set but no scaling_max_freq sysfs on this host"
    fi
  fi
}

restore() {
  [[ -s "$SAVED" ]] || { log "nothing to restore"; return 0; }
  local path value n=0
  while IFS=$'\t' read -r path value; do
    [[ -n "$path" && -n "$value" ]] || continue
    write_sys "$path" "$value" && n=$((n + 1))
  done < "$SAVED"
  log "restored $n cpu sysfs value(s)"
  rm -f "$SAVED"
}

case "${1:-}" in
  apply) apply ;;
  restore) restore ;;
  *) die "usage: $0 apply|restore" ;;
esac
