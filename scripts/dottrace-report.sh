#!/usr/bin/env bash
# Extract and compare dotTrace Reporter XML reports.
#
# Usage:
#   dottrace-report.sh top <report.xml> [N]          Top N functions by OwnTime (default 30)
#   dottrace-report.sh compare <a.xml> <b.xml> [N]   Side-by-side comparison, sorted by delta
#
# Uses grep+awk — parses 70MB files in <2 seconds.

set -uo pipefail

extract() {
    grep '<Function ' "$1" \
        | sed -n 's/.*FQN="\([^"]*\)".*TotalTime="\([^"]*\)".*OwnTime="\([^"]*\)".*/\1\t\2\t\3/p' \
        | awk -F'\t' '{
            own = $3 + 0
            if (!(($1) in best) || own > best[$1]) {
                best[$1] = own; total[$1] = $2
            }
        } END {
            for (fqn in best) printf "%s\t%s\t%s\n", fqn, total[fqn], best[fqn]
        }'
}

fmt_num() {
    awk '{ printf "%\047.0f", $1 }' <<< "$1" 2>/dev/null || echo "$1"
}

cmd_top() {
    local file="$1" n="${2:-30}"
    echo ""
    echo "  Top ${n} by OwnTime — $(basename "$file" .xml)"
    echo ""
    printf "  %-4s  %-65s  %12s  %12s\n" "#" "Function" "OwnTime" "TotalTime"
    printf "  %-4s  %-65s  %12s  %12s\n" "---" "$(printf '%0.s-' {1..65})" "----------" "----------"
    extract "$file" | sort -t$'\t' -k3 -rn | head -n "$n" \
        | awk -F'\t' 'BEGIN { rank=0 } {
            rank++
            printf "  %-4d  %-65s  %12.0f  %12.0f\n", rank, $1, $3, $2
        }'
    echo ""
}

cmd_compare() {
    local file_a="$1" file_b="$2" n="${3:-30}"
    local name_a name_b tmp_a tmp_b
    name_a="$(basename "$file_a" .xml | sed 's/nethermind-multi-//')"
    name_b="$(basename "$file_b" .xml | sed 's/nethermind-multi-//')"
    tmp_a=$(mktemp)
    tmp_b=$(mktemp)

    extract "$file_a" | sort -t$'\t' -k1 > "$tmp_a"
    extract "$file_b" | sort -t$'\t' -k1 > "$tmp_b"

    local joined
    joined=$(join -t$'\t' -j1 "$tmp_a" "$tmp_b" \
        | awk -F'\t' '{
            fqn=$1; a_own=$3+0; b_own=$5+0
            delta = b_own - a_own
            pct = (a_own > 0) ? (delta / a_own * 100) : 999999
            printf "%s\t%.0f\t%.0f\t%.0f\t%.1f\n", fqn, a_own, b_own, delta, pct
        }')

    echo ""
    echo "  Comparison: [A] ${name_a}"
    echo "          vs  [B] ${name_b}"

    echo ""
    echo "  REGRESSIONS (B slower than A) — top ${n} by absolute delta"
    echo ""
    printf "  %-60s  %10s  %10s  %10s  %8s\n" "Function" "[A] Own" "[B] Own" "Delta" "Change"
    printf "  %-60s  %10s  %10s  %10s  %8s\n" "$(printf '%0.s-' {1..60})" "--------" "--------" "--------" "------"

    echo "$joined" | sort -t$'\t' -k4 -rn | head -n "$n" \
        | awk -F'\t' '{
            printf "  %-60s  %10s  %10s  %+10s  %+7.1f%%\n", $1, $2, $3, $4, $5
        }'

    echo ""
    echo "  IMPROVEMENTS (B faster than A) — top ${n} by absolute delta"
    echo ""
    printf "  %-60s  %10s  %10s  %10s  %8s\n" "Function" "[A] Own" "[B] Own" "Delta" "Change"
    printf "  %-60s  %10s  %10s  %10s  %8s\n" "$(printf '%0.s-' {1..60})" "--------" "--------" "--------" "------"

    echo "$joined" | sort -t$'\t' -k4 -n | head -n "$n" \
        | awk -F'\t' '{
            printf "  %-60s  %10s  %10s  %+10s  %+7.1f%%\n", $1, $2, $3, $4, $5
        }'

    echo ""
    rm -f "$tmp_a" "$tmp_b"
}

case "${1:-}" in
    top)     shift; cmd_top "$@" ;;
    compare) shift; cmd_compare "$@" ;;
    *)
        echo "Usage:"
        echo "  $0 top <report.xml> [N]          Top N functions by OwnTime"
        echo "  $0 compare <a.xml> <b.xml> [N]   Compare two reports"
        exit 1
        ;;
esac
