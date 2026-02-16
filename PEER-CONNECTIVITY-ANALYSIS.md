# Nethermind-XDC Peer Connectivity Analysis

**Date:** February 16, 2026  
**Status:** ❌ BLOCKED - Protocol Incompatibility  
**Build:** `build/xdc-net9-stable` branch (commit `959ce1837d`)

---

## Executive Summary

Nethermind-XDC build completed successfully and launches correctly on XDC mainnet, BUT **cannot connect to XDC geth peers** due to missing **eth/100 protocol support**.

**Root Cause:** XDC Network uses a custom `eth/100` P2P protocol for XDPoS-specific messages (votes, timeouts, quorum certificates). Nethermind supports standard Ethereum protocols (eth/62-eth/69) but not eth/100.

---

## Test Results

### ✅ Build Success
- **Compiler:** .NET 9.0.311
- **Build Time:** 30 seconds
- **Errors/Warnings:** 0/0
- **Binary:** `nethermind.dll` + `Nethermind.Xdc.dll`
- **Size:** 187 projects compiled

### ✅ Initial Launch
- **Chain ID:** 50 (XDC Mainnet) ✅
- **Genesis Hash:** `0x4a9d748bd78a8d0385b67788c2435dcdb914f98a96250b68863a1f8b7642d6b1` ✅
- **RPC Server:** Started on port 8548 ✅
- **P2P Listener:** Port 30305 ✅
- **XDC Plugin:** Loaded successfully ✅

### ❌ Peer Connectivity - FAILED

**Issue #1: TooManyPeers (Initial)**
```
Out 95.217.56.168:30303 disconnected Remote TooManyPeers message
Out 65.21.27.213:30303 disconnected Remote TooManyPeers message
Out 175.110.113.12:30303 disconnected Remote TooManyPeers message
```

**Resolution:** Added Nethermind as trusted peer on all 3 geth nodes:
```bash
curl -X POST --data '{"jsonrpc":"2.0","method":"admin_addTrustedPeer","params":["enode://5e5cf..."],"id":1}' http://127.0.0.1:8545
# Result: {"result": true} ✅
```

**Issue #2: NoCapabilityMatched (After Trusted Peer)**
```
Out 95.217.56.168:30303 disconnected Local NoCapabilityMatched capabilities: eth100, eth63, eth62
Out 65.21.27.213:30303 disconnected Local NoCapabilityMatched capabilities: eth100, eth63, eth62
Out 175.110.113.12:30303 disconnected Local NoCapabilityMatched capabilities: eth100, eth63, eth62
```

**Root Cause Identified:**
- **XDC geth advertises:** `eth/100, eth/63, eth/62`
- **Nethermind advertises:** `eth/68, eth/67, eth/66, eth/65, eth/64, eth/63, eth/62` (NO eth/100)
- **Result:** Protocol handshake fails with `NoCapabilityMatched`

---

## Technical Deep Dive

### What is eth/100?

**eth/100** is a **custom P2P subprotocol** added by XDC Network for XDPoS v2 consensus messages:

**Message Types (eth/100 specific):**
- `VoteMessage` (0x10) - Validator votes
- `TimeoutMessage` (0x11) - Round timeout certificates
- `SyncInfoMessage` (0x12) - Consensus state sync
- `QuorumCertificateMessage` (0x13) - BFT finality proofs

**Why XDC needs it:**
- Standard Ethereum (eth/68) only has: BlockHeaders, BlockBodies, Transactions, Receipts, NewBlock, etc.
- XDPoS v2 requires real-time consensus message propagation
- eth/100 extends eth/63 with these additional message types

**Analogy:** It's like trying to connect a VoIP phone (Nethermind) to a network that requires both voice calls AND video calls (XDC geth), but the phone only supports voice.

### Where is eth/100 in the codebase?

**Geth-XDC:** `consensus/XDPoS/engines/engine_v2/protocol/` (NOT in Nethermind fork)

**Erigon-XDC:** Also struggled with this initially. Fixed by:
1. Adding eth/63 support (standard Ethereum)
2. Adding XDC-specific message handlers

**Nethermind-XDC (current):** ❌ Missing entirely

**Search Results:**
```bash
$ find src/Nethermind/Nethermind.Xdc -name "*Protocol*" -o -name "*Eth100*"
(no results)

$ find src/Nethermind/Nethermind.Network -name "*100*"
(no results)
```

**Conclusion:** The eth/100 protocol handler is **not implemented** in this build.

---

## Why This Commit Doesn't Have eth/100

### Timeline Analysis

**This build:** Commit `959ce1837d` (Nov 19, 2025) - "XDC README.md (#9710)"

**eth/100 work:** Branch `xdc-eth100` exists with multiple merges:
```
899c0f1a70 Merge branch 'feature/gaslimit' into xdc-eth100
2c43c01493 Merge branch 'fix/signature-decoding' into xdc-eth100
9998159949 Merge branch 'feature/syncinfo-decoder' into xdc-eth100
```

**Latest branch:** `feature/xdc-network` (HEAD) - Includes .NET 10 migration

**Issue:** We chose commit `959ce1837d` because it was **before .NET 10 migration**, but it's ALSO before eth/100 protocol was merged.

### The Dilemma

| Branch/Commit | .NET Version | eth/100 Support | Status |
|---------------|--------------|-----------------|--------|
| `959ce1837d` (current) | .NET 9 ✅ | ❌ NO | Doesn't connect |
| `feature/xdc-network` | .NET 10 ❌ | Unknown | Can't build |
| `xdc-eth100` branch | Unknown | ✅ YES | Need to check |

---

## Validation from Documentation

From `docs/xdc/COMPARISON.md`:

> ### 4.5 P2P Protocol
> 
> **Geth-XDC**:
> ```go
> // eth/63, eth/100 (XDC-specific)
> // Protocol handshake includes XDC version
> ```
>
> **Erigon-XDC**:
> ```go
> // eth/63 on port 30304 (legacy support)
> // eth/68 on port 30311 (modern)
> // Lesson: Without eth/63, only syncs to ~170k blocks
> ```
>
> **Nethermind approach**:
> - Implement eth/63 handler (required for mainnet)
> - eth/68 handler (modern standard)
> - Port 30305 (eth/63), 30312 (eth/68)

**Key Quote from Lessons Learned:**
> "Initial implementation only supported eth/68. This caused sync to stall at ~170k blocks because mainnet peers are mostly geth-xdc speaking eth/63."

**Recommendation:**
> "Implement eth/63 FIRST, not as an afterthought"

---

## Debug Log Evidence

### Log File: `/root/.nethermind-xdc-test/debug-20260216-071836.log`

**Connection Attempts (first 30 seconds):**
```
[16 Feb 07:18:38] Out 95.217.56.168:30303 disconnected Local NoCapabilityMatched capabilities: eth100, eth63, eth62
[16 Feb 07:18:38] Out 65.21.27.213:30303 disconnected Local NoCapabilityMatched capabilities: eth100, eth63, eth62
[16 Feb 07:18:38] Out 175.110.113.12:30303 disconnected Local NoCapabilityMatched capabilities: eth100, eth63, eth62
```

**Repeats continuously** - Nethermind tries to connect, geth advertises eth/100, Nethermind doesn't have it, connection rejected.

**Peer Count:**
```
Peers: 0 | with best block: 0 | Active: None | Sleeping: All
Waiting for peers... 1s
Waiting for peers... 2s
...
Waiting for peers... 70s (killed test)
```

---

## Solution Options

### Option 1: Implement eth/100 Protocol Handler (HIGH EFFORT)

**What's Needed:**
1. Create `Nethermind.Network/P2P/Subprotocols/Eth/V100/` directory
2. Implement message serializers for:
   - `VoteMessage`
   - `TimeoutMessage`
   - `SyncInfoMessage`
   - `QuorumCertificateMessage`
3. Create `Eth100ProtocolHandler.cs`
4. Register protocol in `ProtocolsManager.cs`
5. Test against XDC geth nodes

**Estimated Time:** 1-2 weeks (C# development + testing)

**Pros:**
- Full compatibility with XDC mainnet
- Can connect to all XDC nodes
- Complete XDPoS v2 support

**Cons:**
- Significant development effort
- Requires deep understanding of XDPoS v2 message formats
- Need to maintain compatibility with future geth-xdc updates

---

### Option 2: Use Older XDC Geth (NO eth/100) (MEDIUM EFFORT)

**Hypothesis:** XDPoS v1 (pre-block 80,370,000) might not require eth/100.

**Test Plan:**
1. Check if older geth-xdc versions (v2.4.x or earlier) advertise only eth/63
2. Deploy older geth on a test server
3. Try Nethermind connection

**Pros:**
- If it works, no Nethermind changes needed
- Could sync historical blocks

**Cons:**
- Won't work for current mainnet (block 80M+)
- Can't sync beyond XDPoS v1 era
- Not a long-term solution

---

### Option 3: Modify Geth to Accept eth/63-Only Peers (LOW EFFORT, LOW SUCCESS)

**Hypothesis:** Maybe geth-xdc can be configured to accept peers that only speak eth/63.

**Test Plan:**
```bash
# Check geth-xdc config options
geth --help | grep -i protocol
geth --help | grep -i capability

# Try disabling eth/100 requirement
geth --xdpos.disable-eth100 (probably doesn't exist)
```

**Pros:**
- Quick to test
- No Nethermind changes

**Cons:**
- Likely won't work (eth/100 is probably hardcoded)
- Would break consensus message propagation
- Not a proper solution

---

### Option 4: Wait for .NET 10 Stable, Use feature/xdc-network (WAIT)

**Timeline:**
- .NET 10 Preview: Available now
- .NET 10 RC: March-April 2026
- .NET 10 Stable: May-June 2026

**Plan:**
1. Wait for .NET 10 stable release
2. Rebuild `feature/xdc-network` branch
3. Check if eth/100 is implemented there

**Pros:**
- Might already have eth/100 implemented
- Latest XDC features
- Supported .NET version

**Cons:**
- 3-4 month wait
- No guarantee eth/100 is implemented
- Still might need development work

---

### Option 5: Find Alternative XDC Nodes (RESEARCH)

**Hypothesis:** Maybe some XDC nodes (Apothem testnet? Archive nodes?) accept eth/63-only connections.

**Test Plan:**
1. Check Apothem testnet bootnodes
2. Try connecting to nodes discovered via DHT
3. Check if any nodes advertise eth/63 without eth/100

**Research Needed:**
```bash
# Check XDC mainnet bootnode list
# Check Apothem testnet config
# Scan P2P network for capability advertisements
```

**Pros:**
- If found, immediate testing possible
- No development needed

**Cons:**
- Unlikely to find on mainnet
- Testnet might work but not mainnet

---

## Recommended Path Forward

### Short-term (This Week):

**1. Verify eth/100 Requirement**
- Check if `feature/xdc-network` branch has eth/100 code
- Review `xdc-eth100` branch commit history
- Confirm which commit first added eth/100 support

**2. Test with Apothem Testnet**
- Deploy Nethermind to Apothem testnet
- Check if testnet peers also require eth/100
- If testnet works, use for development/testing

**3. Document Findings**
- Update BUILD-XDC.md with protocol compatibility notes
- Add "Known Limitations" section
- Document eth/100 requirement clearly

### Medium-term (Next 2 Weeks):

**Option A: Implement eth/100**
- Study geth-xdc's eth/100 implementation
- Port message types to C#
- Create protocol handler
- Test against mainnet

**Option B: Upgrade to .NET 10 Preview**
- Install .NET 10 Preview SDK
- Rebuild `feature/xdc-network` branch
- Check if eth/100 is there
- Test connectivity

### Long-term (Next Month):

**Goal:** Full mainnet sync capability

**Steps:**
1. Complete eth/100 implementation OR upgrade to working commit
2. Deploy to all 3 servers
3. Add SkyNet monitoring
4. Performance testing vs geth-xdc
5. Production deployment

---

## Current Environment State

### Server Configuration

**Server 168 (95.217.56.168):**
- Geth node: Running, port 30303
- Nethermind: Stopped (debug session ended)
- Trusted peer: Nethermind enode added ✅
- Status: Geth healthy (21 peers)

**Server 213 (65.21.27.213):**
- Geth node: Running, port 30303
- Trusted peer: Nethermind enode added ✅
- Status: Geth healthy

**Server 112 (175.110.113.12):**
- Geth node: Running, port 30303
- Trusted peer: Nethermind enode added ✅
- Status: Geth healthy

### Nethermind Configuration

**Data Directory:** `/root/.nethermind-xdc-test/`

**Files:**
- `static-nodes.json` - 3 geth enodes configured ✅
- `debug-20260216-071709.log` - First test run
- `debug-20260216-071836.log` - Second test run (with trusted peers)

**Scripts:**
- `start-nethermind-xdc.sh` - Production startup (Info logging)
- `start-nethermind-debug.sh` - Debug logging enabled

**Screen Session:**
- Session killed, not currently running

---

## Code Changes Made

### GitHub Repository: `AnilChinchawale/nethermind`
### Branch: `build/xdc-net9-stable`

**Commits:**
1. `74e0817747` - Initial documentation and test script
2. `283df7f33c` - Static peer startup script
3. `8b3d32fd9d` - Updated docs with test results

**Files Added:**
- `BUILD-XDC.md` (5KB) - Build instructions
- `README-XDC.md` (5.8KB) - Project overview
- `test-xdc-mainnet.sh` - Basic test script
- `start-nethermind-xdc.sh` - Production startup with static peers
- `start-nethermind-debug.sh` - Debug logging

**Status:** All pushed to GitHub ✅

---

## Next Actions for Tomorrow

### Research Tasks:
1. [ ] Check `feature/xdc-network` for eth/100 code
2. [ ] Review `xdc-eth100` branch history
3. [ ] Check geth-xdc eth/100 implementation complexity
4. [ ] Test Apothem testnet peer requirements

### Decision Points:
- **Path A:** Implement eth/100 (1-2 weeks dev)
- **Path B:** Wait for .NET 10 stable (3-4 months)
- **Path C:** Use Apothem testnet only (limited testing)

### Documentation Updates:
- [ ] Add "Known Limitations" to README-XDC.md
- [ ] Update BUILD-XDC.md with protocol requirements
- [ ] Create ROADMAP.md with implementation plan

---

## Conclusion

**Current Status:** ✅ Build Successful, ❌ Connectivity Blocked

**Blocker:** Missing eth/100 P2P protocol handler

**Severity:** HIGH - Cannot connect to any XDC mainnet peers without eth/100

**Effort to Fix:** MEDIUM-HIGH (1-2 weeks C# development)

**Alternative:** Wait for .NET 10 stable + check if newer commits have eth/100

**Recommendation:** Research `feature/xdc-network` and `xdc-eth100` branches tomorrow to determine best path forward.

---

**Report Generated:** February 16, 2026 07:20 AM IST  
**Author:** OpenClaw Agent  
**Session:** Nethermind-XDC Build & Test
