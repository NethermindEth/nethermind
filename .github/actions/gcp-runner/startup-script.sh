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
curl -fsSL --retry 3 https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-linux-x64.tar.gz -o sdk.tar.gz
echo '1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2  sdk.tar.gz' | sha512sum -c -
tar xzf sdk.tar.gz -C dotnet
rm sdk.tar.gz
curl -sf -H 'Metadata-Flavor: Google' "$MD/attributes/runner-startup-source" -o Startup.cs
curl -sf -H 'Metadata-Flavor: Google' "$MD/attributes/runner-startup-helpers" -o Gcp.Helpers.cs
curl -sf -H 'Metadata-Flavor: Google' "$MD/attributes/runner-provision-helpers" -o StartupProvision.Helpers.cs
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
./dotnet/dotnet publish Startup.cs --no-self-contained -p:PublishAot=false -p:NuGetAudit=false -p:RestoreSources="$BOOTSTRAP_DIR" -o app
./dotnet/dotnet app/Startup.dll
