#!/usr/bin/env bash
# Runs tools/TestSelection with the given arguments, building it once per job. A failed build fails the command, so
# the callers' fallback (run every test) covers it too.
set -euo pipefail

tool="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/test-selection-tool"
if [ ! -f "$tool/TestSelection.dll" ]; then
  dotnet build "$(dirname "$0")/../../tools/TestSelection/TestSelection.csproj" -c Release -o "$tool" --nologo -v q >&2
fi
exec dotnet "$tool/TestSelection.dll" "$@"
