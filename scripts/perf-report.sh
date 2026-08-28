#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Extract and compare folded-stack profiles produced by `perf script | stackcollapse`.
#
# Usage:
#   perf-report.sh top <profile.folded> [N]          Top N frames by self time (default 30)
#   perf-report.sh total <profile.folded> [N]        Top N frames by total (inclusive) time
#   perf-report.sh native <profile.folded> [N]       Top N frames outside managed code
#   perf-report.sh compare <a.folded> <b.folded> [N] Side-by-side, sorted by delta
#
# Folded input is one line per unique stack: "frame1;frame2;...;leaf <count>".
# Self time is the leaf's samples; total time is every stack the frame appears in.
# Counts are samples, reported as a percentage of the profile so two profiles of
# different length stay comparable.
#
# Uses awk — parses multi-hundred-MB profiles in seconds.

set -uo pipefail
export LC_ALL=C

# The runtime perf map writes managed frames as "<ret> [Assembly] Type::Method(args)".
# Everything else - bare C/C++ symbols, kernel symbols, unresolved DSO placeholders - is not.
NATIVE_FILTER='$1 !~ /\[[A-Za-z0-9_.-]+\][[:space:]].*::/'

self_time() {
    awk '{
        n = split($0, parts, " ")
        count = parts[n] + 0
        sub(/ +[0-9]+$/, "")
        m = split($0, frames, ";")
        self[frames[m]] += count
        totalSamples += count
    } END {
        for (f in self) printf "%s\t%d\t%.4f\n", f, self[f], self[f] * 100 / totalSamples
    }' "$1"
}

total_time() {
    awk '{
        n = split($0, parts, " ")
        count = parts[n] + 0
        sub(/ +[0-9]+$/, "")
        m = split($0, frames, ";")
        delete seen
        for (i = 1; i <= m; i++) {
            if (!(frames[i] in seen)) {   # recursion must not double-count
                seen[frames[i]] = 1
                inclusive[frames[i]] += count
            }
        }
        totalSamples += count
    } END {
        for (f in inclusive)
            printf "%s\t%d\t%.4f\n", f, inclusive[f], inclusive[f] * 100 / totalSamples
    }' "$1"
}

require_file() {
    if [[ ! -s "$1" ]]; then
        echo "error: '$1' is missing or empty" >&2
        exit 1
    fi
    # Every view divides by the profile's total sample count. A profile whose counts are all zero,
    # or one truncated mid-write so the last field is not a number, would abort awk with a fatal
    # division by zero instead — and inside compare's process substitution, silently. This stops at
    # the first positive count, so a healthy profile costs one line.
    if ! awk '{ if ($NF + 0 > 0) { found = 1; exit } } END { exit !found }' "$1"; then
        echo "error: '$1' has no positive sample counts" >&2
        exit 1
    fi
}

print_table() {
    local title="$1" n="$2" percentage_label="${3:-Self %}"
    LC_ALL=C sort -t$'\t' -k2 -rn \
        | awk -F'\t' -v title="$title" -v limit="$n" -v percentage_label="$percentage_label" '
            function display_frame(frame, width) {
                return length(frame) > width ? "..." substr(frame, length(frame) - width + 4) : frame
            }
            BEGIN {
                printf "\n  %s\n\n", title
                printf "  %-4s %-72s %10s %8s\n", "#", "Frame", "Samples", percentage_label
                printf "  %-4s %-72s %10s %8s\n", "---", \
                    "------------------------------------------------------------", \
                    "--------", "------"
            }
            NR <= limit { printf "  %-4d %-72s %10d %7.2f%%\n", NR, display_frame($1, 72), $2, $3 }
            END { printf "\n" }'
}

cmd_top() {
    require_file "$1"
    self_time "$1" | print_table "Top ${2:-30} by self time — $(basename "$1")" "${2:-30}"
}

cmd_total() {
    require_file "$1"
    total_time "$1" | print_table "Top ${2:-30} by total time — $(basename "$1")" "${2:-30}" "Total %"
}

cmd_native() {
    require_file "$1"
    self_time "$1" \
        | awk -F'\t' "$NATIVE_FILTER" \
        | print_table "Top ${2:-30} native/unmanaged frames by self time — $(basename "$1")" "${2:-30}"
}

cmd_compare() {
    require_file "$1"
    require_file "$2"
    local n="${3:-30}"
    LC_ALL=C join -t$'\t' -a1 -a2 -e 0 -o '0,1.3,2.3' \
        <(self_time "$1" | LC_ALL=C sort -t$'\t' -k1,1) \
        <(self_time "$2" | LC_ALL=C sort -t$'\t' -k1,1) \
        | awk -F'\t' '
            { delta = $3 - $2; printf "%s\t%.4f\t%.4f\t%.4f\n", $1, $2, $3, delta }' \
        | LC_ALL=C sort -t$'\t' -k4 -g \
        | awk -F'\t' -v n="$n" -v a="$(basename "$1")" -v b="$(basename "$2")" '
            function display_frame(frame, width) {
                return length(frame) > width ? "..." substr(frame, length(frame) - width + 4) : frame
            }
            function print_row(row, fields) {
                split(row, fields, "\t")
                printf "  %-64s %8.2f%% %8.2f%% %+8.2f\n", display_frame(fields[1], 64), fields[2], fields[3], fields[4]
            }
            BEGIN {
                printf "\n  Largest self-time shifts, %s -> %s (percentage points of profile)\n\n", a, b
                printf "  %-64s %9s %9s %9s\n", "Frame", "A %", "B %", "Delta"
                printf "  %-64s %9s %9s %9s\n", \
                    "--------------------------------------------------", \
                    "-------", "-------", "-------"
            }
            { rows[NR] = $0 }
            END {
                half = int((n + 1) / 2)
                if (half < 1) half = 1
                for (i = 1; i <= half && i <= NR; i++) print_row(rows[i])
                start = (NR - half > half ? NR - half + 1 : half + 1)
                if (start > half + 1) printf "  %-64s %9s %9s %9s\n", "...", "", "", ""
                for (i = start; i <= NR; i++) print_row(rows[i])
                printf "\n"
            }'
}

case "${1:-}" in
    top)     shift; cmd_top "$@" ;;
    total)   shift; cmd_total "$@" ;;
    native)  shift; cmd_native "$@" ;;
    compare) shift; cmd_compare "$@" ;;
    *)
        sed -n '5,20p' "$0" | sed 's/^# \{0,1\}//'
        exit 1
        ;;
esac
