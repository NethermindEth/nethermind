#!/usr/bin/env bash
# Host-side perf sampler for one benchmark cell: hardware counters plus a module-level CPU
# split of the node process while the load tool drives it. Counters (GHz, IPC, LLC misses)
# discriminate frequency droop from memory-system saturation; the DSO split attributes the
# CPU that managed-stack profilers report as one opaque [Native code] bucket (RocksDB block
# handling vs GC vs libc memset vs anonymous JIT code).
#
# Best-effort by design: a missing perf binary, an unresolvable container PID or an
# unsupported event writes a note into the output and exits 0, so the measured cell this
# rides along with is never failed by its own diagnostics. Output is symbols and counts
# only — no request content — so it is publishable under the corpus privacy contract.
set -u

CONTAINER="${1:?container name}"
OUT_DIR="${2:?output directory}"
# Delay skips the cell's ramp-up so the windows sample steady state. The three windows run
# sequentially; their sum (plus ~2s of report generation) must fit the shortest measured cell —
# defaults are sized for 30s cells.
DELAY_SECONDS="${PERF_SAMPLE_DELAY_SECONDS:-3}"
STAT_SECONDS="${PERF_STAT_SECONDS:-12}"
RECORD_SECONDS="${PERF_RECORD_SECONDS:-12}"
RECORD_FREQ="${PERF_RECORD_FREQ:-99}"

mkdir -p "$OUT_DIR"
# Notes land in the cell file (published) and on stderr (job log), so a skipped sampler is
# visible while the run is still executing, not only after the artifact is downloaded.
note() { printf '%s\n' "$*" >> "$OUT_DIR/perf-stat.txt"; printf 'perf-sample: %s\n' "$*" >&2; }

if ! command -v perf >/dev/null 2>&1; then
  note "perf not available on this host — sampling skipped"
  exit 0
fi

pid="$(docker inspect -f '{{.State.Pid}}' "$CONTAINER" 2>/dev/null)"
if ! [[ "$pid" =~ ^[0-9]+$ ]] || (( pid <= 0 )); then
  note "could not resolve container '$CONTAINER' to a PID — sampling skipped"
  exit 0
fi

sleep "$DELAY_SECONDS"

# `-p` follows every thread of the node process; `sleep` only bounds the window.
perf stat \
  -e task-clock,cycles,instructions,branches,branch-misses,LLC-loads,LLC-load-misses,LLC-stores,LLC-store-misses,cache-references,cache-misses \
  -p "$pid" -- sleep "$STAT_SECONDS" >> "$OUT_DIR/perf-stat.txt" 2>&1 \
  || note "perf stat failed (unsupported events on this CPU?)"

# Frame-pointer stacks are enough for a DSO split even where native libs lack unwind info;
# perf.data itself is deleted: it is large and the reports carry everything needed.
data="$OUT_DIR/perf.data"
if perf record -F "$RECORD_FREQ" --call-graph fp -o "$data" -p "$pid" -- sleep "$RECORD_SECONDS" >/dev/null 2>&1; then
  # JIT-compiled managed code resolves only when the runtime ran with DOTNET_PerfMapEnabled=1.
  # The runtime names the map after its OWN pid (the container namespace one) while perf looks
  # it up by host pid — bridge with a symlink through the container's /proc root.
  nspid="$(awk '/^NSpid:/ {print $NF}' "/proc/$pid/status" 2>/dev/null)"
  if [[ -n "$nspid" && "$nspid" != "$pid" && -e "/proc/$pid/root/tmp/perf-$nspid.map" ]]; then
    ln -sf "/proc/$pid/root/tmp/perf-$nspid.map" "/tmp/perf-$pid.map" 2>/dev/null \
      || note "perfmap symlink failed — managed symbols unavailable"
  fi
  perf report --stdio -i "$data" --no-children -s dso 2>/dev/null > "$OUT_DIR/perf-dso.txt" \
    || note "perf report (dso) failed"
  perf report --stdio -i "$data" --no-children -s dso,sym 2>/dev/null | head -n 250 > "$OUT_DIR/perf-symbols.txt" \
    || note "perf report (symbols) failed"
  # Folded stacks (root;...;leaf count) for flame/differential charts. Aggregated on the box so
  # the staged file stays small; symbol names only, no request content.
  perf script -i "$data" 2>/dev/null | awk '
    /^[^\t ]/ { if (stack != "") counts[stack]++; stack=""; next }
    /^[\t ]+[0-9a-f]+ / {
      line=$0
      sub(/^[\t ]+[0-9a-f]+ /, "", line)
      sub(/ \([^)]*\)$/, "", line)
      sub(/\+0x[0-9a-f]+$/, "", line)
      stack = (stack == "" ? line : line ";" stack)
    }
    END { if (stack != "") counts[stack]++; for (s in counts) print s, counts[s] }
  ' | sort -t' ' -k2 -rn | head -n 2000 > "$OUT_DIR/perf-folded.txt" \
    || note "stack folding failed"
else
  note "perf record failed — module split unavailable"
fi
rm -f "$data" "$data".old 2>/dev/null

exit 0
