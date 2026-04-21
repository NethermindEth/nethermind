#!/usr/bin/env bash
# Extract and compare dotTrace Reporter XML reports.
#
# Usage:
#   dottrace-report.sh top <report.xml> [N]          Top N functions by OwnTime (default 30)
#   dottrace-report.sh compare <a.xml> <b.xml> [N]   Side-by-side comparison, sorted by delta
#
# The XML format from Reporter.exe is:
#   <Function FQN="Namespace.Class.Method" TotalTime="123" OwnTime="456" Calls="78" />
#
# This script uses grep+awk for speed — parses 70MB files in <1 second.

set -euo pipefail

extract() {
    local file="$1"
    grep -oE 'FQN="[^"]*" TotalTime="[^"]*" OwnTime="[^"]*" Calls="[^"]*"' "$file" \
        | sed 's/FQN="//; s/" TotalTime="/\t/; s/" OwnTime="/\t/; s/" Calls="/\t/; s/"$//'
}

cmd_top() {
    local file="$1"
    local n="${2:-30}"
    echo "Top ${n} functions by OwnTime from $(basename "$file"):"
    echo ""
    printf "%-80s %12s %12s %10s\n" "Function" "OwnTime" "TotalTime" "Calls"
    printf "%-80s %12s %12s %10s\n" "--------" "--------" "---------" "-----"
    extract "$file" | sort -t$'\t' -k3 -rn | head -n "$n" \
        | awk -F'\t' '{ printf "%-80s %12s %12s %10s\n", $1, $3, $2, $4 }'
}

cmd_compare() {
    local file_a="$1"
    local file_b="$2"
    local n="${3:-30}"
    local name_a name_b
    name_a="$(basename "$file_a" .xml)"
    name_b="$(basename "$file_b" .xml)"

    local tmp_a tmp_b
    tmp_a=$(mktemp)
    tmp_b=$(mktemp)
    trap 'rm -f "$tmp_a" "$tmp_b"' EXIT

    extract "$file_a" | sort -t$'\t' -k1 > "$tmp_a"
    extract "$file_b" | sort -t$'\t' -k1 > "$tmp_b"

    echo "Comparison: A=${name_a} vs B=${name_b}"
    echo "Top ${n} regressions (B slower than A) by absolute OwnTime increase:"
    echo ""
    printf "%-70s %10s %10s %10s %8s\n" "Function" "A Own" "B Own" "Delta" "%"
    printf "%-70s %10s %10s %10s %8s\n" "--------" "-----" "-----" "-----" "--"

    join -t$'\t' -j1 "$tmp_a" "$tmp_b" \
        | awk -F'\t' '{
            fqn=$1; a_total=$2; a_own=$3; a_calls=$4; b_total=$5; b_own=$6; b_calls=$7;
            delta = b_own - a_own;
            pct = (a_own > 0) ? (delta / a_own * 100) : 999999;
            printf "%s\t%s\t%s\t%s\t%.1f\n", fqn, a_own, b_own, delta, pct
        }' \
        | sort -t$'\t' -k4 -rn | head -n "$n" \
        | awk -F'\t' '{ printf "%-70s %10s %10s %10s %7.1f%%\n", $1, $2, $3, $4, $5 }'

    echo ""
    echo "Top ${n} improvements (B faster than A):"
    echo ""
    printf "%-70s %10s %10s %10s %8s\n" "Function" "A Own" "B Own" "Delta" "%"
    printf "%-70s %10s %10s %10s %8s\n" "--------" "-----" "-----" "-----" "--"

    join -t$'\t' -j1 "$tmp_a" "$tmp_b" \
        | awk -F'\t' '{
            fqn=$1; a_own=$3; b_own=$6;
            delta = b_own - a_own;
            pct = (a_own > 0) ? (delta / a_own * 100) : -999999;
            printf "%s\t%s\t%s\t%s\t%.1f\n", fqn, a_own, b_own, delta, pct
        }' \
        | sort -t$'\t' -k4 -n | head -n "$n" \
        | awk -F'\t' '{ printf "%-70s %10s %10s %10s %7.1f%%\n", $1, $2, $3, $4, $5 }'
}

case "${1:-}" in
    top)
        shift
        cmd_top "$@"
        ;;
    compare)
        shift
        cmd_compare "$@"
        ;;
    *)
        echo "Usage:"
        echo "  $0 top <report.xml> [N]          Top N functions by OwnTime"
        echo "  $0 compare <a.xml> <b.xml> [N]   Compare two reports"
        exit 1
        ;;
esac
