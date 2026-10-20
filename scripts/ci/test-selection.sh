#!/usr/bin/env bash
# Runs tools/TestSelection with the given arguments, building it once per job. A failed build fails the command, so
# the callers' fallback (run every test) covers it too.
set -euo pipefail

# In CI the build is kept in the job's own temporary directory and reused by its later calls; elsewhere every call
# builds into a fresh directory, so no stale or planted build is ever run.
if [ -n "${RUNNER_TEMP:-}" ]; then
  tool="$RUNNER_TEMP/test-selection-tool"
else
  tool=$(mktemp -d)
fi
if [ ! -f "$tool/TestSelection.dll" ]; then
  dotnet build "$(dirname "$0")/../../tools/TestSelection/TestSelection.csproj" -c Release -o "$tool" --nologo -v q >&2
fi
exec dotnet "$tool/TestSelection.dll" "$@"
