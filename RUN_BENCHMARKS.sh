#!/bin/bash
# Quick reference for running intrinsic gas benchmarks

set -e

DOTNET=/usr/share/dotnet/dotnet
PROJECT=src/Nethermind/Nethermind.Evm.Benchmark

echo "Nethermind Intrinsic Gas Benchmarks"
echo "===================================="
echo ""
echo "This will take 10-30 minutes to complete."
echo ""

# Backup global.json if needed
if [ -f global.json ] && [ ! -f global.json.bak ]; then
    echo "Backing up global.json..."
    mv global.json global.json.bak
fi

# Run benchmarks
echo "Running benchmarks..."
$DOTNET run -c Release --project $PROJECT -- --filter IntrinsicGas* --exporters json,html

# Restore global.json
if [ -f global.json.bak ]; then
    echo "Restoring global.json..."
    mv global.json.bak global.json
fi

echo ""
echo "Done! Results saved to:"
echo "  - BenchmarkDotNet.Artifacts/results/*"
echo ""
echo "Look for:"
echo "  - *-report.html (visual report)"
echo "  - *-report.json (data export)"
