#!/usr/bin/env bash
# Builds the redistributable bundle: one shared library, the native libraries its precompiles
# dlopen, and the C header. The bundle is self-contained — consuming it needs no .NET SDK and no
# Nethermind checkout.
#
# The EVM inside it is compiled from this repository's own src/Nethermind sources, so it is the
# same code the node runs. That only stays true if the binary can be tied back to a revision,
# which is why this script refuses to produce an unstamped bundle.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
VERSION="${NM_EVM_VERSION:-0.1.0}"
RID="linux-x64"
STAGE="$HERE/build/publish"
BUNDLE="${NM_EVM_BUNDLE_DIR:-$HERE/build/nethermind-evm-$VERSION-$RID}"

if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi

die() { echo "error: $*" >&2; exit 1; }
hdr() { printf '\n\033[1m=== %s ===\033[0m\n' "$*"; }

mkdir -p "$HERE/build"

hdr "provenance"
# A bundle is a loose binary: once copied it carries no link back to the tree it came from.
# Resolve the revision here, stamp it into the library, and refuse to ship one that has none.
COMMIT="${NM_EVM_COMMIT:-$(git -C "$REPO" rev-parse HEAD 2>/dev/null)}"
# Three inputs, two states: a tree that cannot be inspected counts as dirty, because
# "we did not check" must never be reported as "clean".
if [ -n "${NM_EVM_DIRTY:-}" ]; then
  DIRTY_FLAG="$NM_EVM_DIRTY"
elif git -C "$REPO" rev-parse --git-dir >/dev/null 2>&1; then
  if [ -n "$(git -C "$REPO" status --porcelain --untracked-files=no)" ]; then
    DIRTY_FLAG=true
  else
    DIRTY_FLAG=false
  fi
else
  DIRTY_FLAG=true
fi
if [ -z "$COMMIT" ]; then
  [ "${NM_EVM_ALLOW_UNSTAMPED:-0}" = "1" ] || die "cannot determine the Nethermind revision.
  $REPO is not a git checkout, so the bundle could not be matched against a node. That is a
  consensus hazard, not an inconvenience: pass NM_EVM_COMMIT=<sha>, or set
  NM_EVM_ALLOW_UNSTAMPED=1 to accept a bundle nobody can tie to a node revision."
  COMMIT="unknown"
fi
printf '  commit   %s\n' "$COMMIT"
printf '  dirty    %s\n' "$DIRTY_FLAG"
if [ "$DIRTY_FLAG" = "true" ]; then
  echo "  the tree is modified or could not be inspected, so this binary matches no"
  echo "  published revision and must not be paired with a released node"
fi

hdr "publish (NativeAOT)"
rm -rf "$STAGE"
dotnet publish "$HERE/Nethermind.Evm.Ffi/Nethermind.Evm.Ffi.csproj" \
  -c Release -r "$RID" \
  -p:NmEvmCommit="$COMMIT" -p:NmEvmVersion="$VERSION" -p:NmEvmDirty="$DIRTY_FLAG" \
  -o "$STAGE" > "$HERE/build/publish.log" 2>&1
rc=$?
grep -E "warning IL" "$HERE/build/publish.log" | sed 's/^/  /' | head -10
[ $rc -eq 0 ] && [ -f "$STAGE/nethermind_evm.so" ] || {
  grep -E " error " "$HERE/build/publish.log" | head -20
  die "publish failed (log: $HERE/build/publish.log)"
}

hdr "assemble bundle"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/lib" "$BUNDLE/include"

# The ABI library, under the name a linker expects from -lnethermind_evm.
cp "$STAGE/nethermind_evm.so" "$BUNDLE/lib/libnethermind_evm.so"
cp "$HERE/include/nethermind_evm.h" "$BUNDLE/include/"

# The precompiles P/Invoke these, lazily, on first use of the matching precompile. They are
# listed explicitly rather than globbed so that anything new shows up as a failing smoke test
# instead of silently riding along.
for native in libblst.so libsecp256k1.so libsecp256r1.so libgmp.so libmcl.so ckzg.so; do
  if [ -f "$STAGE/$native" ]; then
    cp "$STAGE/$native" "$BUNDLE/lib/"
  else
    echo "  note: $native not in the publish output, skipping"
  fi
done
# c-kzg reads this when the KZG precompile first runs.
[ -f "$STAGE/kzg_trusted_setup.txt" ] && cp "$STAGE/kzg_trusted_setup.txt" "$BUNDLE/lib/"

# Deliberately excluded, both arriving through Nethermind.Blockchain's package graph and both
# unreachable from the EVM: ClearScriptV8 (~58 MB, the JS tracer's V8) and libic.so (~6 MB,
# TurboPFor integer compression for receipt storage). Neither is dlopened unless something calls
# into it, and nothing here does. Together they are three quarters of a naive publish output.

cat > "$BUNDLE/MANIFEST" <<EOF
name       nethermind-evm
version    $VERSION
abi        $(grep -oP '#define NM_EVM_ABI_VERSION \K[0-9]+' "$HERE/include/nethermind_evm.h")
rid        $RID
built      $(date -u +%Y-%m-%dT%H:%M:%SZ)
commit     $COMMIT
dirty      $DIRTY_FLAG
EOF

hdr "bundle"
sed 's/^/  /' "$BUNDLE/MANIFEST"
echo
du -sh "$BUNDLE" | sed 's/^/  /'
ls -la "$BUNDLE/lib" | tail -n +4 | awk '{printf "  %10s  %s\n", $5, $9}'

hdr "exports"
nm -D --defined-only "$BUNDLE/lib/libnethermind_evm.so" | awk '/nm_evm/ {print "  " $3}'

# The manifest is a convenience sitting next to the binary; the library is the authority, because
# it travels with the code. Prove it actually reports what was stamped in.
hdr "what the library says about itself"
cat > "$HERE/build/provenance.c" <<'CEOF'
#include <dlfcn.h>
#include <stdio.h>
int main(int argc, char **argv) {
    void *h = dlopen(argv[1], RTLD_NOW);
    if (!h) { fprintf(stderr, "%s\n", dlerror()); return 1; }
    int (*info)(char *, int) = (int (*)(char *, int))dlsym(h, "nm_evm_build_info");
    if (!info) { fprintf(stderr, "nm_evm_build_info missing\n"); return 1; }
    char buf[512];
    info(buf, sizeof buf);
    printf("  %s\n", buf);
    return 0;
}
CEOF
if gcc -o "$HERE/build/provenance" "$HERE/build/provenance.c" -ldl 2>/dev/null; then
  "$HERE/build/provenance" "$BUNDLE/lib/libnethermind_evm.so" \
    || die "the library does not report its provenance"
else
  echo "  (gcc unavailable, skipped)"
fi

hdr "unresolved link-time dependencies"
ldd "$BUNDLE/lib/libnethermind_evm.so" | sed 's/^/  /'

echo
echo "bundle ready: $BUNDLE"
echo "consume it with:  export NETHERMIND_EVM_DIR=$BUNDLE"
