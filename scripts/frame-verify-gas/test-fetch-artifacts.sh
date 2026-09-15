#!/usr/bin/env bash
# Runs fetch-artifacts.sh against fake releases served by a stub gh. Needs bash, GNU tar, python3, sha256sum.
set -uo pipefail

SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/fetch-artifacts.sh"
readonly SCRIPT
readonly LABELS=(236k 300k 500k soispoke)
root=$(mktemp -d)
trap 'rm -rf "${root}"' EXIT
mkdir -p "${root}/bin" "${root}/releases" "${root}/runs"
pass=0
fail=0

cat > "${root}/bin/gh" <<'EOF'
#!/usr/bin/env bash
echo "gh $*" >> "${STUB_LOG}"
release="${STUB_RELEASES}/$3"
if [[ ! -d "${release}" ]]; then
  echo "release not found" >&2
  exit 1
fi
case "$1 $2" in
  'release view') cat "${release}/state" ;;
  'release download')
    shift 3
    dir=''
    patterns=()
    while (($#)); do
      case "$1" in
        --dir) dir=$2; shift 2 ;;
        --pattern) patterns+=("$2"); shift 2 ;;
        *) shift ;;
      esac
    done
    matched=0
    for pattern in "${patterns[@]}"; do
      if [[ -f "${release}/assets/${pattern}" ]]; then
        cp "${release}/assets/${pattern}" "${dir}/"
        matched=1
      fi
    done
    if (( ! matched )); then
      echo "no assets match the file pattern" >&2
      exit 1
    fi
    ;;
esac
EOF
chmod +x "${root}/bin/gh"

hex_repeat() {
  local out='' i
  for ((i = 0; i < $2; i++)); do out+=$1; done
  printf '%s' "${out}"
}
GOOD_VERIFIER="0x600660076008fa$(hex_repeat 5b 1100)"
readonly GOOD_VERIFIER

# release <tag> <state> <pre-hook> <post-hook>: hooks get the sweep source dir and the assets dir.
release() {
  local tag=$1 state=$2 pre=$3 post=$4 label
  local src="${root}/src/${tag}" assets="${root}/releases/${tag}/assets"
  mkdir -p "${assets}"
  echo "${state}" > "${root}/releases/${tag}/state"
  for label in "${LABELS[@]}"; do
    mkdir -p "${src}/sweep-${label}"
    echo "${GOOD_VERIFIER}" > "${src}/sweep-${label}/verifier.hex"
    echo "0x$(hex_repeat 00 36)" > "${src}/sweep-${label}/calldata-invalid.hex"
    echo 234190 > "${src}/sweep-${label}/gas.txt"
  done
  "${pre}" "${src}" "${assets}"
  for label in "${LABELS[@]}"; do
    [[ -e "${assets}/sweep-${label}.tar.gz" ]] || tar -czf "${assets}/sweep-${label}.tar.gz" -C "${src}" "sweep-${label}"
  done
  (cd "${assets}" && sha256sum sweep-*.tar.gz > SHA256SUMS)
  "${post}" "${src}" "${assets}"
}

# expect <case> <version> <exit code> <output regex>
expect() {
  local name=$1 version=$2 want_rc=$3 want=$4
  local run="${root}/runs/${name//[^A-Za-z0-9_-]/_}"
  mkdir -p "${run}/temp"
  : > "${run}/github_env"
  : > "${run}/gh.log"
  env -i HOME="${HOME}" PATH="${root}/bin:${PATH}" RUNNER_TEMP="${run}/temp" GITHUB_ENV="${run}/github_env" \
    STUB_RELEASES="${root}/releases" STUB_LOG="${run}/gh.log" GH_TOKEN=stub REQUESTED_VERSION="${version}" \
    "${SCRIPT}" > "${run}/out" 2>&1
  local rc=$?
  if [[ "${rc}" == "${want_rc}" ]] && grep -qE -- "${want}" "${run}/out"; then
    pass=$((pass + 1))
    printf 'PASS %-26s %s\n' "${name}" "$(grep -m1 -E -- "${want}" "${run}/out")"
  else
    fail=$((fail + 1))
    printf 'FAIL %-26s rc=%s want=%s\n' "${name}" "${rc}" "${want_rc}"
    sed 's/^/     /' "${run}/out"
  fi
  LAST_RUN="${run}"
}

check() {
  if eval "$2"; then
    pass=$((pass + 1)); echo "PASS $1"
  else
    fail=$((fail + 1)); echo "FAIL $1"
  fi
}

none() { :; }
dot_root() { mkdir "$1/only"; mv "$1/sweep-236k" "$1/only/"; tar -czf "$2/sweep-236k.tar.gz" -C "$1/only" .; }
dot_prefix() { tar -czf "$2/sweep-236k.tar.gz" -C "$1" ./sweep-236k; }
tamper() { printf 'x' >> "$2/sweep-300k.tar.gz"; }
drop_sum() { sed -i '$d' "$2/SHA256SUMS"; }
extra_sum() { echo "$(hex_repeat 0 64)  sweep-evil.tar.gz" >> "$2/SHA256SUMS"; }
duplicate_sum() { sed -i '1p;$d' "$2/SHA256SUMS"; }
no_sums() { rm "$2/SHA256SUMS"; }
no_tarball() { rm "$2/sweep-500k.tar.gz"; }
dotdot() { tar -czf "$2/sweep-236k.tar.gz" -C "$1" --transform 's,^sweep-236k/gas.txt$,sweep-236k/../gas.txt,' sweep-236k; }
absolute() { tar -czPf "$2/sweep-236k.tar.gz" -C "$1" --transform 's,^sweep-236k/gas.txt$,/tmp/fetch-artifacts-test-escape,' sweep-236k; }
foreign() { tar -czf "$2/sweep-236k.tar.gz" -C "$1" sweep-236k sweep-300k/gas.txt; }
bare_file() { tar -czf "$2/sweep-236k.tar.gz" -C "$1/sweep-236k" gas.txt verifier.hex calldata-invalid.hex; }
symlink() { ln -sf /etc/hostname "$1/sweep-236k/verifier.hex"; }
symlink_dir() { ln -s /tmp "$1/sweep-236k/source"; }
hardlink() { ln "$1/sweep-236k/gas.txt" "$1/sweep-236k/gas-link.txt"; }
fifo() { mkfifo "$1/sweep-236k/pipe"; }
newline_name() { echo x > "$1/sweep-236k/evil"$'\n'"name"; }
too_big() { head -c $((16 * 1024 * 1024 + 1)) /dev/zero > "$1/sweep-236k/padding.bin"; }
no_gas() { rm "$1/sweep-236k/gas.txt"; }
verifier() { local body=$1; eval "set_verifier() { echo '${body}' > \"\$1/sweep-236k/verifier.hex\"; }"; }
set_file() { eval "set_${1//-/_}() { printf '%s' '$3' > \"\$1/sweep-236k/$2\"; }"; }

release v1.0.0 'false false' none none
release v1.0.1 'false false' dot_root none
release v1.0.2 'false false' dot_prefix none
release v1.1.0 'true false' none none
release v1.2.0 'false true' none none
release v2.0.0 'false false' none tamper
release v2.0.1 'false false' none drop_sum
release v2.0.2 'false false' none extra_sum
release v2.0.3 'false false' none duplicate_sum
release v2.0.4 'false false' none no_sums
release v2.0.5 'false false' none no_tarball
release v3.0.0 'false false' dotdot none
release v3.0.1 'false false' absolute none
release v3.0.2 'false false' foreign none
release v3.0.3 'false false' bare_file none
release v3.0.4 'false false' symlink none
release v3.0.5 'false false' symlink_dir none
release v3.0.6 'false false' hardlink none
release v3.0.7 'false false' fifo none
release v3.0.8 'false false' newline_name none
release v3.0.9 'false false' too_big none
release v3.0.10 'false false' no_gas none
verifier "0x$(hex_repeat 5b 1100)"; release v4.0.0 'false false' set_verifier none
verifier "0x$(hex_repeat 616006 150)$(hex_repeat 616007 150)$(hex_repeat 616008 150)fa"; release v4.0.1 'false false' set_verifier none
verifier "0x600660076008f1$(hex_repeat 5b 1100)"; release v4.0.2 'false false' set_verifier none
verifier "0x600660076008fa$(hex_repeat 5b 100)"; release v4.0.3 'false false' set_verifier none
verifier "0x600660076008fa$(hex_repeat 5b 24600)"; release v4.0.4 'false false' set_verifier none
verifier "0x600660076008f"; release v4.0.5 'false false' set_verifier none
set_file calldata calldata-invalid.hex 0xzz; release v4.0.6 'false false' set_calldata none
set_file short calldata-invalid.hex 0x0102; release v4.0.7 'false false' set_short none
set_file gas gas.txt 12a4; release v4.0.8 'false false' set_gas none

expect happy-path v1.0.0 0 'extracted to'
happy="${LAST_RUN}"
check happy-path-env "grep -qx 'FRAME_GROTH16_ARTIFACTS=${happy}/temp/frame-verify-gas-groth16' '${happy}/github_env' \
  && grep -qx 'GROTH16_ARTIFACTS_VERSION=v1.0.0' '${happy}/github_env' \
  && grep -qE '^GROTH16_ARTIFACTS_SHA256SUMS_DIGEST=[0-9a-f]{64}\$' '${happy}/github_env'"
check happy-path-tree "diff -r '${root}/src/v1.0.0' '${happy}/temp/frame-verify-gas-groth16' \
  && grep -qx 'GROTH16_ARTIFACTS_SHA256SUMS=${happy}/temp/frame-verify-gas-groth16.SHA256SUMS' '${happy}/github_env' \
  && cmp -s '${root}/releases/v1.0.0/assets/SHA256SUMS' '${happy}/temp/frame-verify-gas-groth16.SHA256SUMS'"
check happy-path-no-download-dir-left "[[ -z \$(find '${happy}/temp' -maxdepth 1 -name 'frame-verify-gas-download.*') ]]"
expect dot-slash-root v1.0.1 0 'extracted to'
expect dot-slash-prefix v1.0.2 0 'extracted to'
expect draft v1.1.0 1 'is a draft'
expect prerelease v1.2.0 1 'is a prerelease'
expect nonexistent-tag v9.9.9 1 'could not be read from .*release not found'
for bad in latest v1.2 v1.2.3-rc1 ../x v01.2.3 1.2.3 'v1.2.3;id'; do
  expect "bad-version:${bad}" "${bad}" 1 'is not a vMAJOR.MINOR.PATCH release tag'
  check "bad-version:${bad}:no-gh-call" "[[ ! -s '${LAST_RUN}/gh.log' ]]"
done
expect empty-version '' 1 'groth16_artifacts_version is empty'
expect blank-version '  ' 1 'groth16_artifacts_version is empty'
expect tampered-tarball v2.0.0 1 'do not match SHA256SUMS .* corrupt, partial or was altered'
expect sums-missing-entry v2.0.1 1 'SHA256SUMS of release v2.0.1 must list exactly'
expect sums-extra-entry v2.0.2 1 'SHA256SUMS of release v2.0.2 must list exactly'
expect sums-duplicate-entry v2.0.3 1 'SHA256SUMS of release v2.0.3 must list exactly'
expect sums-asset-missing v2.0.4 1 'has no SHA256SUMS asset'
expect tarball-asset-missing v2.0.5 1 'has no sweep-500k.tar.gz asset'
expect tar-dotdot v3.0.0 1 "unexpected path or prefix \\('sweep-236k/\\.\\./gas\\.txt'\\)"
expect tar-absolute v3.0.1 1 "unexpected path or prefix \\('/tmp/fetch-artifacts-test-escape'\\)"
expect tar-foreign-sweep v3.0.2 1 "unexpected path or prefix \\('sweep-300k/gas\\.txt'\\)"
expect tar-no-sweep-dir v3.0.3 1 "unexpected path or prefix \\('gas\\.txt'\\)"
expect tar-symlink v3.0.4 1 'link or special-file member'
expect tar-symlink-dir v3.0.5 1 'link or special-file member'
expect tar-hardlink v3.0.6 1 'link or special-file member'
expect tar-fifo v3.0.7 1 'link or special-file member'
expect tar-newline-name v3.0.8 1 'unexpected path or prefix'
expect tar-over-16mib v3.0.9 1 'expands to 1677[0-9]+ bytes, over the 16777216-byte limit'
expect tar-missing-gas v3.0.10 1 'has no sweep-236k/gas.txt'
# Only the absolute-path escape is checkable here: validation runs on the tar listing before any
# mkdir/extract for that sweep (see fetch-artifacts.sh), so a rejected dotdot member is never written
# anywhere to begin with, and fetch-artifacts.sh's own work-dir trap would remove it regardless — the
# regression that matters is already caught by tar-dotdot's own exit-code and message assertion above.
check no-escaped-files "[[ ! -e /tmp/fetch-artifacts-test-escape ]]"
expect verifier-no-precompile v4.0.0 1 'rejected sweep-236k/verifier.hex \(precompile-push check\): no PUSH1 of 0x06 ecAdd, 0x07 ecMul, 0x08 ecPairing'
check verifier-no-precompile:names-heuristic "grep -q 'plausibility heuristic in scripts/frame-verify-gas/check-verifiers.py rejected release v4.0.0' '${LAST_RUN}/out'"
expect verifier-push-data-only v4.0.1 1 'rejected sweep-236k/verifier.hex \(precompile-push check\)'
expect verifier-no-staticcall v4.0.2 1 'rejected sweep-236k/verifier.hex \(staticcall check\)'
expect verifier-too-small v4.0.3 1 'rejected sweep-236k/verifier.hex \(size check\): 107 bytes'
expect verifier-too-big v4.0.4 1 'rejected sweep-236k/verifier.hex \(size check\): 24607 bytes'
expect verifier-odd-hex v4.0.5 1 'rejected sweep-236k/verifier.hex \(hex check\)'
expect calldata-not-hex v4.0.6 1 'rejected sweep-236k/calldata-invalid.hex \(hex check\)'
expect calldata-too-short v4.0.7 1 'rejected sweep-236k/calldata-invalid.hex \(length check\)'
expect gas-not-integer v4.0.8 1 'rejected sweep-236k/gas.txt \(integer check\)'

echo "pass=${pass} fail=${fail}"
(( fail == 0 ))
