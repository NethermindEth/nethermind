# Testing XDC Mainnet Configuration Fix

This document describes how to test the `xdc-mainnet.json` configuration fix for issue #41.

## Problem Statement
Nethermind was exiting with error:
```
Configuration file not found: /nethermind/configs/xdc-mainnet
```

## Solution
Created `xdc-mainnet.json` configuration file in `src/Nethermind/Nethermind.Runner/configs/`

## Test Plan

### Test 1: Verify Config File Exists

```bash
# Check config file exists in source
ls -l src/Nethermind/Nethermind.Runner/configs/xdc-mainnet.json

# Expected: File exists with ~1952 bytes
```

### Test 2: Validate JSON Syntax

```bash
# Validate JSON is well-formed
cat src/Nethermind/Nethermind.Runner/configs/xdc-mainnet.json | jq '.'

# Expected: Valid JSON output with no errors
```

### Test 3: Build Nethermind

```bash
export PATH="/usr/local/dotnet:$PATH"
cd src/Nethermind/Nethermind.Runner
dotnet build -c Release

# Expected: Build succeeds, config file is copied to artifacts/
```

### Test 4: Verify Config in Artifacts

```bash
# Check config is in build output
ls -l src/Nethermind/artifacts/bin/Nethermind.Runner/release/configs/xdc-mainnet.json

# Expected: File exists in artifacts directory
```

### Test 5: Test with run-xdc-mainnet.sh Script

```bash
# This script uses --config xdc-mainnet
./run-xdc-mainnet.sh

# Expected: Nethermind starts without "Configuration file not found" error
# Expected: Logs show: "Using config file: configs/xdc-mainnet.json"
```

### Test 6: Test with Docker Build

```bash
# Build Docker image
docker build -f Dockerfile.xdc -t nethermind-xdc:test .

# Run with xdc-mainnet config
docker run --rm nethermind-xdc:test \
  --config xdc-mainnet \
  --help

# Expected: No "Configuration file not found" error
```

### Test 7: Verify Config Content

```bash
# Check the config file has correct mainnet settings
cat src/Nethermind/Nethermind.Runner/configs/xdc-mainnet.json | jq '.Init.ChainSpecPath'

# Expected: "chainspec/xdc.json" (mainnet chainspec)

cat src/Nethermind/Nethermind.Runner/configs/xdc-mainnet.json | jq '.Init.GenesisHash'

# Expected: "0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1"
```

### Test 8: Compare with xdc.json

```bash
# Verify xdc-mainnet.json is identical to xdc.json (mainnet config)
diff src/Nethermind/Nethermind.Runner/configs/xdc.json \
     src/Nethermind/Nethermind.Runner/configs/xdc-mainnet.json

# Expected: No differences (files are identical)
```

### Test 9: Test with dotnet run

```bash
cd src/Nethermind/Nethermind.Runner

dotnet run -c Release -- \
  --config xdc-mainnet \
  --data-dir /tmp/test-xdc-mainnet \
  --JsonRpc.Enabled true \
  --JsonRpc.Port 8549 \
  --help

# Expected: Help output displays without config error
```

### Test 10: Full Integration Test

```bash
cd src/Nethermind/artifacts/bin/Nethermind.Runner/release

# Start Nethermind with xdc-mainnet config
dotnet nethermind.dll \
  --config xdc-mainnet \
  --data-dir /tmp/xdc-mainnet-test \
  --JsonRpc.Enabled true \
  --JsonRpc.Port 8549 \
  --Network.P2PPort 30306 \
  --Network.DiscoveryPort 30306 \
  --log Info

# Expected behaviors:
# 1. ✅ Starts without "Configuration file not found" error
# 2. ✅ Loads chainspec from chainspec/xdc.json
# 3. ✅ Genesis hash matches: 0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1
# 4. ✅ Network peers connect (static peers from config)
# 5. ✅ Sync begins from block 0 or pivot
# 6. ✅ RPC responds on port 8549
```

## Verification Checklist

- [x] Config file created: `xdc-mainnet.json`
- [x] Config file committed to git
- [x] Config file pushed to remote
- [x] JSON syntax validated
- [x] Config copied to artifacts directory
- [ ] Build test passed (requires .NET 9 SDK)
- [ ] Docker build test passed
- [ ] Runtime test with --config xdc-mainnet flag passed
- [ ] Sync test with mainnet completed

## Expected Outcomes

### Before Fix
```
2026-02-25 06:00:00.0000|ERROR|Configuration file not found: /nethermind/configs/xdc-mainnet
Exit code: 1
```

### After Fix
```
2026-02-25 06:08:00.0000|INFO|Using configuration file: configs/xdc-mainnet.json
2026-02-25 06:08:00.0001|INFO|Loading chainspec from: chainspec/xdc.json
2026-02-25 06:08:00.0002|INFO|Genesis hash: 0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1
2026-02-25 06:08:00.0003|INFO|XDC Mainnet - Chain ID: 50
2026-02-25 06:08:00.0004|INFO|Starting P2P discovery on port 30303
2026-02-25 06:08:00.0005|INFO|RPC server listening on 0.0.0.0:8545
```

## Troubleshooting

### Issue: Config file not found after build
**Solution:** Check if config is in source directory, rebuild, verify artifacts

### Issue: JSON parsing error
**Solution:** Validate JSON with `jq`, check for syntax errors

### Issue: Wrong genesis hash
**Solution:** Verify config points to correct chainspec (chainspec/xdc.json for mainnet)

### Issue: Sync not starting
**Solution:** Check network peers, verify P2P port is open, check logs

## Files Involved

- **Config file (source):** `src/Nethermind/Nethermind.Runner/configs/xdc-mainnet.json`
- **Config file (artifacts):** `src/Nethermind/artifacts/bin/Nethermind.Runner/release/configs/xdc-mainnet.json`
- **Chainspec:** `src/Nethermind/Nethermind.Runner/chainspec/xdc.json`
- **Launch script:** `run-xdc-mainnet.sh`
- **Docker file:** `Dockerfile.xdc`

## Next Steps

1. ✅ Config file created and committed
2. ✅ Changes pushed to repository
3. ⏳ Build and test in .NET environment
4. ⏳ Deploy to production
5. ⏳ Monitor sync progress
6. ⏳ Update documentation if needed

## Git Information

- **Branch:** build/xdc-net9-stable
- **Commit:** b0a710e33a
- **Commit Message:** "Add xdc-mainnet.json configuration file"
- **Date:** 2026-02-25

---

**Status:** ✅ **FIX IMPLEMENTED** - Ready for testing

The configuration file fix is complete. Testing should be performed in an environment with .NET 9 SDK installed.
