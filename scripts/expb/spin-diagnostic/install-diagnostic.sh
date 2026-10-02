#!/usr/bin/env bash
set -euo pipefail
umask 077
expected=797db21d602901e35dad6c2c5aa7b570f614d7ff
[[ "${EXPB_REPO}" == NethermindEth/execution-payloads-benchmarks && "${EXPB_BRANCH}" == "${expected}" ]] || { echo 'Unexpected EXPB source'; exit 1; }
bundle="${GITHUB_WORKSPACE}/scripts/expb/spin-diagnostic"
source_dir="${RUNNER_TEMP}/spin-expb-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"
[[ ! -e "${source_dir}" ]] || { echo 'Diagnostic source directory exists'; exit 1; }
git clone --no-checkout --filter=blob:none https://github.com/NethermindEth/execution-payloads-benchmarks "${source_dir}"
git -c core.autocrlf=false -c core.eol=lf -C "${source_dir}" checkout --detach "${expected}"
[[ "$(git -C "${source_dir}" rev-parse HEAD)" == "${expected}" ]]
[[ -z "$(git -C "${source_dir}" status --porcelain)" ]]
(cd "${bundle}" && sha256sum --check bundle.sha256)
for patch in executor-integration.patch runtime-trace.patch request-output.patch client-identity.patch; do
  git -c core.autocrlf=false -c core.eol=lf -C "${source_dir}" apply --check "${bundle}/${patch}"
  git -c core.autocrlf=false -c core.eol=lf -C "${source_dir}" apply "${bundle}/${patch}"
done
for module in scheduler_collector.py limited_exec.py pidfd_compat.py; do
  cp -- "${bundle}/${module}" "${source_dir}/src/expb/payloads/executor/${module}"
done
python3 -m compileall -q "${source_dir}/src/expb"
uv tool install --force --from "${source_dir}" expb
expb_bin="$(uv tool dir --bin)/expb"
expb_python="$(head -n1 "${expb_bin}")"
[[ "${expb_python}" == '#!'/* && "${expb_python}" != *' '* ]] || { echo 'Unexpected EXPB interpreter shebang'; exit 1; }
expb_python="${expb_python#'#!'}"
[[ -x "${expb_python}" ]] || { echo 'EXPB interpreter unavailable'; exit 1; }
"${expb_python}" -I "${bundle}/pidfd_compat.py" --self-check > "${RUNNER_TEMP}/spin-pidfd-capability-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}.json"
echo "EXPB_SOURCE=git+https://github.com/${EXPB_REPO}@${expected}+spin-diagnostic-${GITHUB_SHA}" >> "${GITHUB_ENV}"
echo "EXPB_DIAGNOSTIC_SOURCE=${source_dir}" >> "${GITHUB_ENV}"
git -C "${source_dir}" diff --check
