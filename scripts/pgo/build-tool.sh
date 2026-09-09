#!/usr/bin/env bash
set -euo pipefail
: "${1:?usage: build-tool.sh NEW_RUNTIME_CHECKOUT}"
checkout=$(realpath -m "$1")
git clone --depth 1 --branch v10.0.11 https://github.com/dotnet/runtime.git "$checkout"
test "$(git -C "$checkout" rev-parse HEAD)" = 79d0c463f1b55624c874a11585f7e47731e8d675
python3 "$(dirname "${BASH_SOURCE[0]}")/patch-tool.py" "$checkout"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_NOLOGO=1 MSBuildEnableWorkloadResolver=false
export DOTNET_CLI_HOME="$checkout/.vs/dotnet-cli-home"
cd "$checkout"
./build.sh -c Release --projects "$checkout/src/coreclr/tools/dotnet-pgo/dotnet-pgo.csproj"
