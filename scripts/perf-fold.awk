# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Fold `perf script` output into "frame;frame;... count" lines, one per unique
# stack, ordered leaf-last. Consumed by scripts/perf-report.sh.
#
#   perf script -i perf.data | awk -f scripts/perf-fold.awk > perf.folded
#
# Frame lines are indented and leaf-first: "\t<addr> <symbol>+0x<off> (<dso>)".
# The address and offset are noise once symbolized; an unresolved symbol keeps its
# DSO so unrelated stacks do not collapse into a single "[unknown]".

function flush() {
    if (depth > 0) {
        stack = comm
        for (i = depth; i >= 1; i--)
            stack = (stack == "" ? frames[i] : stack ";" frames[i])
        counts[stack]++
    }
    depth = 0
}

function label(symbol, dso) {
    if (symbol != "" && symbol != "[unknown]")
        return symbol
    if (dso == "" || dso == "[unknown]")
        return "[unknown]"
    sub(/^.*\//, "", dso)
    return "[unknown] (" dso ")"
}

BEGIN { depth = 0; comm = "" }

# Blank line terminates a sample.
/^[[:space:]]*$/ { flush(); next }

# Indented line: one frame.
/^[[:space:]]/ {
    line = $0
    dso = ""
    # Strip first: with trailing whitespace inside the match, RLENGTH - 2 would leave the closing
    # paren in the name, and "[unknown] (libcoreclr.so))" would split one library's samples in two.
    sub(/[[:space:]]+$/, "", line)
    if (match(line, /\([^)]*\)$/)) {
        dso = substr(line, RSTART + 1, RLENGTH - 2)
        line = substr(line, 1, RSTART - 1)
    }
    # Drop the leading address, then a trailing +0x<offset>.
    sub(/^[[:space:]]+[0-9a-fA-F]+[[:space:]]+/, "", line)
    sub(/\+0x[0-9a-fA-F]+[[:space:]]*$/, "", line)
    sub(/[[:space:]]+$/, "", line)
    frames[++depth] = label(line, dso)
    next
}

# Anything else starts a new sample; its first field is the command name.
{ flush(); comm = $1 }

END {
    flush()
    for (stack in counts) print stack, counts[stack]
}
