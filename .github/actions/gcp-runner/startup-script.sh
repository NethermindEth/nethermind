#!/usr/bin/env bash
# A fresh GCE image has no .NET SDK. Verify and install the pinned SDK, then hand off all provisioning to C#.
set -euo pipefail
MD=http://metadata.google.internal/computeMetadata/v1/instance
fail() {
  local status=$?
  if [[ $(curl -sf --max-time 5 -H 'Metadata-Flavor: Google' "$MD/guest-attributes/gh-runner/stage" || true) != failed ]]; then
    curl -sf -X PUT --data "bootstrap line ${BASH_LINENO[0]}: exit ${status}" -H 'Metadata-Flavor: Google' "$MD/guest-attributes/gh-runner/error" >/dev/null || true
  fi
  curl -sf -X PUT --data failed -H 'Metadata-Flavor: Google' "$MD/guest-attributes/gh-runner/stage" >/dev/null || true
  exit "$status"
}
trap fail ERR
curl -sf -X PUT --data bootstrap -H 'Metadata-Flavor: Google' "$MD/guest-attributes/gh-runner/stage" >/dev/null || true
BOOTSTRAP_DIR=${BOOTSTRAP_DIR:-/opt/gh-runner-bootstrap}
export HOME="${HOME:-$BOOTSTRAP_DIR/home}" DOTNET_CLI_HOME="$BOOTSTRAP_DIR"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mkdir -p "$BOOTSTRAP_DIR/dotnet" "$HOME"
cd "$BOOTSTRAP_DIR"
case $(uname -m) in
  x86_64) sdk_arch=x64 sdk_sha512=1bec9cca2ec689b1e80245b84f2b53c9441d1aa6daccf2ceb27490768bffa95954fa585bd275def31069d874d543d9ba9e6591fe9ade2dbd9f34ec5c0fd0050c ;;
  aarch64) sdk_arch=arm64 sdk_sha512=cfa248bb1377606e868602fb5b4d66fa16b7546156593937d1982f7f2142e6ded76d71c4a26efdab99f87c46ab40c3c672ab1b57ef7fa25449efe5b140a55af4 ;;
  *) false ;;
esac
curl -fsSL --retry 3 "https://builds.dotnet.microsoft.com/dotnet/Sdk/11.0.100/dotnet-sdk-11.0.100-linux-${sdk_arch}.tar.gz" -o sdk.tar.gz
echo "${sdk_sha512}  sdk.tar.gz" | sha512sum -c -
tar xzf sdk.tar.gz -C dotnet
rm sdk.tar.gz
curl -sf -H 'Metadata-Flavor: Google' "$MD/attributes/runner-startup-source" -o startup.cs
curl -sf -H 'Metadata-Flavor: Google' "$MD/attributes/runner-startup-helpers" -o gcp.helpers.cs
curl -sf -H 'Metadata-Flavor: Google' "$MD/attributes/runner-provision-helpers" -o startup-provision.helpers.cs
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
./dotnet/dotnet publish startup.cs --no-self-contained -p:PublishAot=false -p:NuGetAudit=false -p:RestoreSources="$BOOTSTRAP_DIR" -o app
./dotnet/dotnet app/startup.dll
