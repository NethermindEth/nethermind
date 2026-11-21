#!/usr/bin/env bash
set -uo pipefail
solution=$1
logs=$2
shift 2
mkdir -p "$logs"
log="$logs/$(printf '%s' "$solution" | sed 's#/#__#g').log"
echo "build: dotnet build $solution -c Release $*"
dotnet build "$solution" -c Release -p:TreatWarningsAsErrors=false -p:EnforceCodeStyleInBuild=true \
  -p:RunAnalyzersDuringBuild=true -p:GenerateDocumentationFile=true -clp:NoSummary "$@" 2>&1 | grep -E '\): (warning|error) [A-Z]+[0-9]+:|^Build (succeeded|FAILED)|error MSB' | sort -u > "$log"
rc=${PIPESTATUS[0]}
echo "build: $solution exit $rc, $(grep -c '): warning' "$log") warning lines"
grep -q '^Build succeeded' "$log" || { echo "build: $solution did not succeed"; exit 1; }
