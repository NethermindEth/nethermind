#!/usr/bin/env bash
# Benchmarks Nethermind's EVM published as a NativeAOT shared library and driven from Rust,
# against revm on the same machine. See README.md.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="${EVM_BENCH_OUT:-$HOME/.cache/evm-native-bench}"
NATIVE_PROJ="$HERE/Nethermind.Evm.Native/Nethermind.Evm.Native.csproj"
TRACE_PROJ="$HERE/TraceHarness/TraceHarness.csproj"
LIB="Nethermind.Evm.Native.so"

# A locally installed SDK wins: distro packages are often older than global.json's floor.
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
fi
export PATH="$HOME/.cargo/bin:$PATH"

die() { echo "error: $*" >&2; exit 1; }
hdr() { printf '\n\033[1m=== %s ===\033[0m\n' "$*"; }

cmd_check() {
  hdr "prerequisites"
  command -v dotnet >/dev/null || die "dotnet not found; see README"
  printf '%-14s %s\n' dotnet "$(dotnet --version 2>/dev/null)"
  dotnet --list-sdks | awk '{print "               sdk " $1}'
  command -v cargo >/dev/null && printf '%-14s %s\n' cargo "$(cargo --version)" \
    || echo "cargo         MISSING  (curl https://sh.rustup.rs -sSf | sh -s -- -y --no-modify-path)"
  command -v gcc >/dev/null && printf '%-14s %s\n' gcc "$(gcc -dumpversion)" \
    || echo "gcc           MISSING  (NativeAOT needs a C toolchain to link)"
  for f in /usr/lib/x86_64-linux-gnu/libz.a /usr/lib/gcc/x86_64-linux-gnu/*/libstdc++.a; do
    [ -e "$f" ] && printf '%-14s %s\n' "static lib" "$f"
  done
  command -v dotnet-trace >/dev/null && printf '%-14s %s\n' dotnet-trace "$(dotnet-trace --version 2>/dev/null | head -1)" \
    || echo "dotnet-trace  MISSING  (dotnet tool install --global dotnet-trace) — only needed for 'trace'"
  case "$REPO" in
    /mnt/*) echo; echo "NOTE: the repo is on a Windows drive. Builds and I/O are much slower there;"
            echo "      copy the checkout to a Linux-native path before trusting any timing." ;;
  esac
}

cmd_publish() {
  hdr "publish the EVM as a native shared library"
  mkdir -p "$OUT"
  rm -f "$OUT/$LIB"                       # never let a failed publish leave a stale library behind
  dotnet publish "$NATIVE_PROJ" -c Release -r linux-x64 -o "$OUT" > "$OUT/publish.log" 2>&1
  local rc=$?
  grep -E "warning IL" "$OUT/publish.log" | sed 's/^/  /' | head -20
  [ $rc -eq 0 ] && [ -f "$OUT/$LIB" ] || {
    grep -E " error " "$OUT/publish.log" | head -20
    die "publish failed (full log: $OUT/publish.log)"
  }
  # NativeAOT emits <AssemblyName>.so with no lib prefix; -l wants lib<name>.so.
  ln -sf "$OUT/$LIB" "$OUT/lib$LIB"
  printf '  %s  %s\n' "$(du -h "$OUT/$LIB" | cut -f1)" "$OUT/$LIB"
  nm -D --defined-only "$OUT/$LIB" | awk '/nm_evm/ {print "  export " $3}'
}

cmd_nethermind() {
  cmd_publish || exit 1
  hdr "Nethermind EVM, driven from Rust"
  ( cd "$HERE/rust/evmcaller" \
    && EVM_LIB_DIR="$OUT" cargo build --release 2>&1 | grep -E "^error|Finished" | sed 's/^/  /' \
    && LD_LIBRARY_PATH="$OUT" ./target/release/evmcaller )
}

cmd_revm() {
  hdr "revm baseline"
  ( cd "$HERE/rust/revmbase" \
    && cargo build --release 2>&1 | grep -E "^error|Finished" | sed 's/^/  /' \
    && ./target/release/revmbase )
}

cmd_trace() {
  hdr "allocation trace (JIT harness)"
  command -v dotnet-trace >/dev/null || die "dotnet tool install --global dotnet-trace"
  mkdir -p "$OUT"
  dotnet build "$TRACE_PROJ" -c Release 2>&1 | grep -E " error |Build FAILED" && die "build failed"
  local bin
  bin="$(find "$REPO" -path '*/artifacts/bin/TraceHarness/release/TraceHarness.dll' | head -1)"
  [ -n "$bin" ] || die "TraceHarness.dll not found after build"

  echo "  baseline, no tracer:"
  dotnet "$bin" 0 200000 | grep MEASURE.END | sed 's/^/    /'
  dotnet "$bin" 1 20000  | grep MEASURE.END | sed 's/^/    /'

  echo "  collecting GCAllocationTick ..."
  rm -f "$OUT/alloc.nettrace"
  dotnet-trace collect --format NetTrace -o "$OUT/alloc.nettrace" --profile gc-verbose \
    -- dotnet "$bin" 0 200000 >/dev/null 2>&1
  hdr "allocation by type"
  dotnet run "$HERE/scripts/alloc-report.cs" -- "$OUT/alloc.nettrace" --top 15 2>/dev/null
  hdr "top allocation sites (stacks)"
  dotnet run "$HERE/scripts/alloc-stacks.cs" -- "$OUT/alloc.nettrace" --top 5 2>/dev/null
}

case "${1:-all}" in
  check)      cmd_check ;;
  publish)    cmd_publish ;;
  nethermind) cmd_nethermind ;;
  revm)       cmd_revm ;;
  trace)      cmd_trace ;;
  all)        cmd_check; cmd_nethermind; cmd_revm ;;
  *) echo "usage: $0 [check|publish|nethermind|revm|trace|all]" >&2; exit 2 ;;
esac
