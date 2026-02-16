# eth/100 Protocol Integration Plan for Nethermind (XDC Network)

**Document Version:** 1.0  
**Date:** 2026-02-16  
**Status:** Architectural Design Complete  
**Author:** Nethermind XDC Integration Team

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Current State Analysis](#2-current-state-analysis)
3. [Protocol Registration Flow](#3-protocol-registration-flow)
4. [Factory Implementation](#4-factory-implementation)
5. [Serializer Registration](#5-serializer-registration)
6. [Consensus Integration](#6-consensus-integration)
7. [Handshake & Status Exchange](#7-handshake--status-exchange)
8. [Configuration](#8-configuration)
9. [Implementation Steps (Ordered)](#9-implementation-steps-ordered)
10. [Testing Strategy](#10-testing-strategy)
11. [Reference Implementations](#11-reference-implementations)
12. [Risks & Mitigations](#12-risks--mitigations)

---

## 1. Executive Summary

XDC Network uses a custom P2P protocol designated `eth/100` (internally `XDPOS2`). It extends Ethereum's `eth/63` with four additional XDPoS v2 consensus message types:

| Code | Message | Purpose |
|------|---------|---------|
| `0x11` | **Vote** | Validator vote for proposed block |
| `0x12` | **Timeout** | Round timeout from validator |
| `0x13` | **SyncInfo** | Consensus state synchronization (QC + TC) |
| `0x14` | **QuorumCertificate** | BFT finality proof |

The protocol uses `eth/62`-style status handshake (**no ForkID**) and advertises capabilities `[eth/62, eth/63, eth/100]` during devp2p capability negotiation.

**Key insight from geth-xdc reference:**
- `eth/100` (XDPOS2) protocol length is **227 message IDs** (`protocolLengths[XDPOS2] = 227`)
- Consensus messages use high codes: Vote=`0xe0`, Timeout=`0xe1`, SyncInfo=`0xe2`
- However, our existing Nethermind handler uses codes `0x11-0x14` with `MessageIdSpaceSize = 21`

> ⚠️ **CRITICAL DIVERGENCE**: The geth-xdc reference uses message codes `0xe0-0xe2` with a 227-message namespace, while our current `Eth100ProtocolHandler` uses `0x11-0x14` with a 21-message namespace. We must align with geth-xdc for interoperability. **See Section 6.1 for resolution.**

---

## 2. Current State Analysis

### 2.1 What Already Exists

The codebase already has substantial eth/100 infrastructure:

**In `Nethermind.Xdc` project:**
```
Nethermind.Xdc/
├── P2P/Eth100/
│   ├── Eth100MessageCode.cs          ← Message code constants (0x11-0x14)
│   ├── Eth100ProtocolHandler.cs      ← Handler extends Eth63ProtocolHandler
│   └── Messages/
│       ├── VoteP2PMessage.cs         ← P2P message wrapper
│       ├── VoteP2PMessageSerializer.cs
│       ├── TimeoutP2PMessage.cs
│       ├── TimeoutP2PMessageSerializer.cs
│       ├── SyncInfoP2PMessage.cs
│       ├── SyncInfoP2PMessageSerializer.cs
│       ├── QuorumCertificateP2PMessage.cs
│       └── QuorumCertificateP2PMessageSerializer.cs
├── Types/
│   ├── Vote.cs, Timeout.cs, SyncInfo.cs, QuorumCertificate.cs
├── RLP/
│   ├── VoteDecoder.cs, TimeoutDecoder.cs, SyncInfoDecoder.cs, QuorumCertificateDecoder.cs
├── XdcConsensusMessageProcessor.cs   ← Routes P2P → consensus
├── IXdcConsensusMessageProcessor.cs  ← Interface
└── XdcModule.cs                      ← DI registrations (missing P2P/network)
```

**In `Nethermind.Network` project:**
```
Nethermind.Network/P2P/Subprotocols/Eth/V100/  ← Empty directory (placeholder)
```

### 2.2 What's Missing

1. **Protocol registration** – `ProtocolsManager` doesn't know about eth/100
2. **Capability advertisement** – `DefaultCapabilities` doesn't include `new Capability("eth", 100)`
3. **Serializer registration** – `NetworkModule` doesn't register the 4 XDC message serializers
4. **EthVersions constant** – No `Eth100 = 100` in `EthVersions.cs`
5. **Handler instantiation** – The `GetProtocolFactories()` switch doesn't have a `case 100:`
6. **XdcModule P2P wiring** – `XdcModule.cs` doesn't register `IXdcConsensusMessageProcessor`
7. **Handshake adaptation** – eth/100 needs `eth/62`-style handshake (no ForkID)
8. **Message code alignment** – Current codes `0x11-0x14` need verification against geth-xdc (`0xe0-0xe2`)

---

## 3. Protocol Registration Flow

### 3.1 How Nethermind Registers Protocols (Existing Pattern)

```
                                    ProtocolsManager
                                         │
                                         │ DefaultCapabilities
                                         │ ┌──────────────────────────┐
                                         │ │ ("eth", 66)              │
                                         │ │ ("eth", 67)              │
                                         │ │ ("eth", 68)              │
                                         │ │ ("nodedata", 1)          │
                                         │ └──────────────────────────┘
                                         │
                    ┌────────────────────┼──────────────────────┐
                    │                    │                      │
              SessionCreated      SessionInitialized      InitProtocol
                    │                    │                      │
                    │                    │           GetProtocolFactories()
                    │                    │                      │
                    │                    │              Protocol.Eth switch:
                    │                    │              ├── 66 → Eth66Handler
                    │                    │              ├── 67 → Eth67Handler
                    │                    │              ├── 68 → Eth68Handler
                    │                    │              └── 69 → Eth69Handler
                    │                    │
              _sessions.Add      AddSupportedCapability
                                  for each in _capabilities
```

### 3.2 Required Changes for eth/100

**Step 1: Add EthVersions.Eth100**

File: `Nethermind.Network.Contract/P2P/EthVersions.cs`

```csharp
public static class EthVersions
{
    public const byte Eth62 = 62;
    public const byte Eth63 = 63;
    // ...
    public const byte Eth69 = 69;
    public const byte Eth100 = 100;  // ← ADD: XDC eth/100 (XDPoS v2)
}
```

**Step 2: Modify DefaultCapabilities in ProtocolsManager**

File: `Nethermind.Network/ProtocolsManager.cs`

For XDC, the default capabilities need to be:
```csharp
// XDC Network capabilities (when running XDC chain):
new Capability(Protocol.Eth, 62),   // eth/62 (XDC compatible)
new Capability(Protocol.Eth, 63),   // eth/63 (XDC compatible)
new Capability(Protocol.Eth, 100),  // eth/100 (XDPoS v2 consensus)
```

This should NOT replace Ethereum-mainnet defaults. The mechanism to swap capabilities should be:

**Option A (Recommended): XdcPlugin modifies capabilities at startup**
```csharp
// In XdcPlugin or XdcModule initialization:
protocolsManager.RemoveSupportedCapability(new Capability(Protocol.Eth, 66));
protocolsManager.RemoveSupportedCapability(new Capability(Protocol.Eth, 67));
protocolsManager.RemoveSupportedCapability(new Capability(Protocol.Eth, 68));
protocolsManager.AddSupportedCapability(new Capability(Protocol.Eth, 62));
protocolsManager.AddSupportedCapability(new Capability(Protocol.Eth, 63));
protocolsManager.AddSupportedCapability(new Capability(Protocol.Eth, 100));
```

**Option B: Conditional DefaultCapabilities based on chain spec**
```csharp
// ProtocolsManager constructor detects XDC from ISpecProvider/ChainSpec
if (isXdc)
    _capabilities = xdcCapabilities;
else
    _capabilities = DefaultCapabilities;
```

**Recommendation: Option A** – keeps ProtocolsManager generic, plugin modifies at startup.

**Step 3: Add eth/100 to GetProtocolFactories()**

File: `Nethermind.Network/ProtocolsManager.cs` → `GetProtocolFactories()`

```csharp
[Protocol.Eth] = (session, version) =>
{
    // ... existing switch
    Eth66ProtocolHandler ethHandler = version switch
    {
        66 => new Eth66ProtocolHandler(...),
        67 => new Eth67ProtocolHandler(...),
        68 => new Eth68ProtocolHandler(...),
        69 => new Eth69ProtocolHandler(...),
        _ => throw new NotSupportedException(...)
    };
    // ...
}
```

**Problem:** `Eth100ProtocolHandler` lives in `Nethermind.Xdc`, not `Nethermind.Network`. ProtocolsManager cannot directly reference it without creating a circular dependency.

**Solution: Use `AddProtocol()` method from XdcPlugin**

`ProtocolsManager` already has:
```csharp
public void AddProtocol(string code, Func<ISession, IProtocolHandler> factory)
```

But we need version-aware registration. The actual approach should be:

**Solution: Plugin registers a factory via AddSupportedCapability + custom factory**

Looking at the code flow more carefully, `InitProtocol` resolves by protocol code (e.g., `"eth"`), not by version. The version is passed to the factory function. So for eth/100, the existing `[Protocol.Eth]` factory needs to handle version `100`.

**The cleanest approach: Inject an `IXdcConsensusMessageProcessor` into ProtocolsManager and add the switch case conditionally.**

However, since `Nethermind.Network` shouldn't depend on `Nethermind.Xdc`, we need a factory pattern:

### 3.3 Factory Registration Architecture

```
┌──────────────┐     registers factory      ┌─────────────────────┐
│   XdcPlugin  │ ────────────────────────── │  ProtocolsManager   │
│              │   AddProtocol("eth", ...)  │                     │
└──────────────┘                            │  _protocolFactories │
       │                                    │  ["eth"] = factory  │
       │ resolves                           └──────────┬──────────┘
       ▼                                               │
┌──────────────────────┐                    On session, version=100
│ Eth100ProtocolHandler │ ◀─────────────── factory(session, 100)
│ (in Nethermind.Xdc)  │
└──────────────────────┘
```

**Approach: Override the `"eth"` protocol factory from XdcPlugin to include version 100:**

In `XdcModule.cs` or a new `XdcNetworkModule.cs`:

```csharp
// Register an extended factory that handles both standard eth versions AND eth/100
builder.Register<IProtocolsManager>((ctx) => {
    var pm = ctx.Resolve<ProtocolsManager>();
    // The existing eth factory already handles 66-69
    // We need to extend it to also handle 100
    // Use AddProtocol or a decorator pattern
});
```

**Actually, the simplest approach:** Modify `ProtocolsManager.GetProtocolFactories()` to support a `Func<ISession, int, IProtocolHandler>?` optional override for the eth protocol, injected via constructor or method.

**Final Recommended Approach:**

1. Add an `IEth100ProtocolFactory` interface in `Nethermind.Network`:
```csharp
public interface IEth100ProtocolFactory
{
    IProtocolHandler Create(ISession session);
}
```

2. ProtocolsManager checks for it in the switch statement:
```csharp
100 => _eth100Factory?.Create(session) 
    ?? throw new NotSupportedException("eth/100 requires XDC plugin"),
```

3. XdcModule registers `IEth100ProtocolFactory` → `Eth100ProtocolFactory`

This keeps dependencies clean: `Nethermind.Network` defines the interface, `Nethermind.Xdc` implements it.

---

## 4. Factory Implementation

### 4.1 Interface (in Nethermind.Network)

```csharp
// File: Nethermind.Network/P2P/Subprotocols/Eth/V100/IEth100ProtocolFactory.cs
namespace Nethermind.Network.P2P.Subprotocols.Eth.V100
{
    /// <summary>
    /// Factory for creating eth/100 protocol handlers.
    /// Implemented by chain-specific plugins (e.g., XDC).
    /// </summary>
    public interface IEth100ProtocolFactory
    {
        /// <summary>
        /// Creates an eth/100 protocol handler for the given session.
        /// </summary>
        SyncPeerProtocolHandlerBase Create(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager stats,
            ISyncServer syncServer,
            IBackgroundTaskScheduler scheduler,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            ILogManager logManager,
            ITxGossipPolicy? txGossipPolicy);
    }
}
```

### 4.2 Implementation (in Nethermind.Xdc)

```csharp
// File: Nethermind.Xdc/P2P/Eth100/Eth100ProtocolFactory.cs
using Nethermind.Consensus;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V100;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;

namespace Nethermind.Xdc.P2P.Eth100
{
    public class Eth100ProtocolFactory : IEth100ProtocolFactory
    {
        private readonly IXdcConsensusMessageProcessor? _consensusProcessor;

        public Eth100ProtocolFactory(IXdcConsensusMessageProcessor? consensusProcessor = null)
        {
            _consensusProcessor = consensusProcessor;
        }

        public SyncPeerProtocolHandlerBase Create(
            ISession session,
            IMessageSerializationService serializer,
            INodeStatsManager stats,
            ISyncServer syncServer,
            IBackgroundTaskScheduler scheduler,
            ITxPool txPool,
            IGossipPolicy gossipPolicy,
            ILogManager logManager,
            ITxGossipPolicy? txGossipPolicy)
        {
            return new Eth100ProtocolHandler(
                session, serializer, stats, syncServer, scheduler,
                txPool, gossipPolicy, logManager,
                _consensusProcessor, txGossipPolicy);
        }
    }
}
```

### 4.3 ProtocolsManager Integration

```csharp
// In ProtocolsManager constructor, add optional parameter:
private readonly IEth100ProtocolFactory? _eth100Factory;

public ProtocolsManager(
    // ... existing parameters ...
    IEth100ProtocolFactory? eth100Factory = null)  // ← optional
{
    _eth100Factory = eth100Factory;
    // ...
}

// In GetProtocolFactories(), modify the eth switch:
[Protocol.Eth] = (session, version) =>
{
    SyncPeerProtocolHandlerBase ethHandler = version switch
    {
        66 => new Eth66ProtocolHandler(...),
        67 => new Eth67ProtocolHandler(...),
        68 => new Eth68ProtocolHandler(...),
        69 => new Eth69ProtocolHandler(...),
        100 => _eth100Factory?.Create(session, _serializer, _stats, _syncServer,
                   _backgroundTaskScheduler, _txPool, _gossipPolicy, _logManager, _txGossipPolicy)
               ?? throw new NotSupportedException("eth/100 requires XDC plugin"),
        _ => throw new NotSupportedException($"Eth protocol version {version} is not supported.")
    };

    InitSyncPeerProtocol(session, ethHandler);
    return ethHandler;
}
```

---

## 5. Serializer Registration

### 5.1 How Serializers Are Registered (Existing Pattern)

In `Nethermind.Init/Modules/NetworkModule.cs`, serializers are registered via:
```csharp
builder.AddMessageSerializer<MessageType, SerializerType>()
```

This calls `ContainerBuilderExtensions.AddMessageSerializer<T,S>()` which:
1. Registers `IZeroMessageSerializer<T>` → `S` as singleton
2. Creates a `SerializerInfo(typeof(T), instance)` for `MessageSerializationService`

### 5.2 XDC Serializer Registration

The 4 XDC message serializers should be registered in `XdcModule.cs` (not `NetworkModule.cs`), since they belong to the XDC plugin:

```csharp
// File: Nethermind.Xdc/XdcModule.cs - add to Load() method:
using Xdc100 = Nethermind.Xdc.P2P.Eth100.Messages;

// eth/100 P2P message serializers
builder
    .AddMessageSerializer<Xdc100.VoteP2PMessage, Xdc100.VoteP2PMessageSerializer>()
    .AddMessageSerializer<Xdc100.TimeoutP2PMessage, Xdc100.TimeoutP2PMessageSerializer>()
    .AddMessageSerializer<Xdc100.SyncInfoP2PMessage, Xdc100.SyncInfoP2PMessageSerializer>()
    .AddMessageSerializer<Xdc100.QuorumCertificateP2PMessage, Xdc100.QuorumCertificateP2PMessageSerializer>()

// eth/100 protocol factory
    .AddSingleton<IXdcConsensusMessageProcessor, XdcConsensusMessageProcessor>()
    .AddSingleton<IEth100ProtocolFactory, Eth100ProtocolFactory>()
    ;
```

**Note:** `AddMessageSerializer` is an extension method on `ContainerBuilder` defined in `Nethermind.Network/ContainerBuilderExtensions.cs`. The `Nethermind.Xdc` project must reference `Nethermind.Network` (it already does, since `Eth100ProtocolHandler` extends `Eth63ProtocolHandler`).

### 5.3 Registration Flow Diagram

```
Startup
   │
   ├── NetworkModule.Load()
   │   └── Registers standard eth V62-V69 serializers
   │
   ├── XdcPlugin detected (SealEngineType == "XDPoS")
   │   └── XdcModule.Load()
   │       ├── Registers XDC consensus components
   │       ├── Registers 4 eth/100 message serializers
   │       │   ├── VoteP2PMessage → VoteP2PMessageSerializer
   │       │   ├── TimeoutP2PMessage → TimeoutP2PMessageSerializer
   │       │   ├── SyncInfoP2PMessage → SyncInfoP2PMessageSerializer
   │       │   └── QuorumCertificateP2PMessage → QuorumCertificateP2PMessageSerializer
   │       └── Registers Eth100ProtocolFactory
   │
   └── ProtocolsManager constructed
       ├── Resolves IEth100ProtocolFactory (from XdcModule)
       ├── Includes version 100 in protocol factory switch
       └── XDC capabilities added: eth/62, eth/63, eth/100
```

---

## 6. Consensus Integration

### 6.1 Message Code Alignment (CRITICAL)

**Current Nethermind (Eth100MessageCode.cs):**
```csharp
Vote = 0x11;              // 17
Timeout = 0x12;           // 18
SyncInfo = 0x13;          // 19
QuorumCertificate = 0x14; // 20
MessageIdSpaceSize = 21;  // codes 0x00-0x14
```

**geth-xdc reference (protocol.go):**
```go
VoteMsg     = 0xe0   // 224
TimeoutMsg  = 0xe1   // 225
SyncInfoMsg = 0xe2   // 226
protocolLengths[XDPOS2] = 227
```

**Resolution:** In Nethermind's P2P implementation, `PacketType` is relative to the protocol's base offset. The `MessageIdSpaceSize` determines how many message codes are reserved. In geth, the full 227-slot namespace includes sparse assignments.

In devp2p capability negotiation, each protocol gets a message ID offset. The message codes within a protocol are **relative** (0-based). So:
- geth-xdc sends Vote as absolute `baseOffset + 0xe0`
- Nethermind needs to match: `PacketType` must return `0xe0` (224)

**Required Fix:**
```csharp
// Eth100MessageCode.cs - MUST align with geth-xdc
public static class Eth100MessageCode
{
    // Standard eth/63 messages (0x00-0x10) handled by base class
    
    // XDPoS v2 consensus messages - aligned with geth-xdc
    public const int Vote = 0xe0;              // 224
    public const int Timeout = 0xe1;           // 225
    public const int SyncInfo = 0xe2;          // 226
}

// Eth100ProtocolHandler
public override int MessageIdSpaceSize => 227; // Must match geth-xdc protocolLengths[XDPOS2]
```

> **Note:** geth-xdc does NOT have a separate QuorumCertificate message. QCs are embedded in block headers and SyncInfo messages. We should remove the standalone QC P2P message to match geth-xdc, OR keep it for future Nethermind-to-Nethermind optimization but not expect geth peers to understand it.

### 6.2 Message Flow: P2P → Consensus Engine

```
     ┌──────────────┐
     │   P2P Layer  │
     │ (RLPx/TCP)   │
     └──────┬───────┘
            │ ZeroPacket
            ▼
     ┌──────────────────────────┐
     │  Eth100ProtocolHandler   │
     │  HandleMessage(packet)   │
     │                          │
     │  switch(packet.PacketType)│
     │  ├── 0xe0 → Vote        │
     │  ├── 0xe1 → Timeout     │
     │  ├── 0xe2 → SyncInfo    │
     │  └── default → base.HandleMessage()
     └──────┬───────────────────┘
            │ Deserialized types
            ▼
     ┌──────────────────────────────────────┐
     │   IXdcConsensusMessageProcessor      │
     │   (XdcConsensusMessageProcessor)     │
     │                                      │
     │   ProcessVote(vote)     → VotesManager         → XdcPool<Vote>
     │   ProcessTimeout(to)    → TimeoutCertManager    → XdcPool<Timeout>
     │   ProcessSyncInfo(si)   → SyncInfoManager       → Update context
     │   ProcessQuorumCert(qc) → QCManager             → Update HighestQC
     └──────────────────────────────────────┘
            │
            ▼
     ┌──────────────────────────────────────┐
     │   XdcConsensusContext                │
     │   (State machine)                    │
     │                                      │
     │   CurrentRound, HighestQC, LockQC,   │
     │   HighestTC, HighestCommitBlock      │
     └──────────────────────────────────────┘
            │
            ▼
     ┌──────────────────────────────────────┐
     │   XdcHotStuff                        │
     │   (Consensus orchestrator)           │
     │   - Advances rounds                  │
     │   - Triggers block production        │
     │   - Finalizes blocks                 │
     └──────────────────────────────────────┘
```

### 6.3 Broadcasting Consensus Messages (Outbound)

The `Eth100ProtocolHandler` provides broadcast methods:

```csharp
BroadcastVote(Vote vote)          → Send VoteP2PMessage to peer
BroadcastTimeout(Timeout timeout) → Send TimeoutP2PMessage to peer
SendSyncInfo(SyncInfo syncInfo)   → Send SyncInfoP2PMessage to peer
```

These need to be callable from the consensus engine. The pattern:

```
XdcHotStuff / VotesManager
        │
        │ needs to broadcast to all peers
        ▼
ISyncPeerPool → iterate peers → cast to Eth100ProtocolHandler → BroadcastVote()
```

**Implementation:** Add an `IXdcBroadcaster` interface:

```csharp
public interface IXdcBroadcaster
{
    void BroadcastVote(Vote vote);
    void BroadcastTimeout(Timeout timeout);
    void BroadcastSyncInfo(SyncInfo syncInfo);
}
```

The broadcaster iterates over connected peers and sends to each `Eth100ProtocolHandler`.

---

## 7. Handshake & Status Exchange

### 7.1 XDC Handshake Differences

XDC uses an **eth/62-style handshake** without ForkID:

| Field | Standard eth/66+ | XDC eth/100 |
|-------|-------------------|-------------|
| ProtocolVersion | ✅ | ✅ (100) |
| NetworkId | ✅ | ✅ (50 for mainnet) |
| TD | ✅ | ✅ (block-number-based) |
| BestHash | ✅ | ✅ |
| GenesisHash | ✅ | ✅ |
| ForkID | ✅ | ❌ NOT PRESENT |

### 7.2 StatusMessage Handling

The existing `Eth100ProtocolHandler` extends `Eth63ProtocolHandler` which extends `Eth62ProtocolHandler`. The `Eth62ProtocolHandler.NotifyOfStatus()` already creates a StatusMessage **without ForkID** by default. The `EnrichStatusMessage()` virtual method is where eth/64+ adds ForkID.

Since `Eth100ProtocolHandler` extends `Eth63ProtocolHandler` (which extends `Eth62` directly, NOT `Eth64`), it **already omits ForkID**. This is correct.

The `StatusMessageSerializer` already handles the optional ForkID:
```csharp
// Deserialize: only reads ForkID if there are remaining bytes
if (rlpStream.Position < rlpStream.Length)
{
    // Read ForkID
}
```

**No changes needed for handshake serialization.**

### 7.3 Protocol Version in Status Message

The `Eth100ProtocolHandler.ProtocolVersion` returns `100`. When the status message is sent:
```csharp
statusMessage.ProtocolVersion = ProtocolVersion;  // = 100
```

This matches geth-xdc's behavior:
```go
pkt := &StatusPacket62{
    ProtocolVersion: uint32(p.version),  // = 100
    // ...
}
```

**Verified: Compatible. ✅**

### 7.4 TD (Total Difficulty) Handling

XDC is pre-merge (no Proof-of-Stake). TD is meaningful. geth-xdc uses block number as TD:
```go
td := new(big.Int).SetUint64(latest.Number.Uint64())
```

Nethermind's `SyncServer.Head.TotalDifficulty` should work correctly since XDC headers contain difficulty. If TD is null, it falls back to `head.Difficulty`:
```csharp
TotalDifficulty = head.TotalDifficulty ?? head.Difficulty
```

**Verify:** XDC sets `Difficulty = 1` for all blocks (`XdcConstants.DifficultyDefault = UInt256.One`), so TD = block number. This should be correct.

---

## 8. Configuration

### 8.1 Chain Spec (`xdc.json`)

Already exists at `src/Nethermind/Chains/xdc.json` with XDPoS engine parameters.

### 8.2 Runner Config (`configs/xdc.json`)

Current:
```json
{
  "Init": {
    "ChainSpecPath": "chainspec/xdc.json",
    "BaseDbPath": "nethermind_db/xdc",
    "LogFileName": "xdc.log"
  },
  "TxPool": { "BlobsSupport": "Disabled" },
  "Sync": {
    "FastSync": true,
    "SnapSync": true,
    "PivotNumber": 80370000
  }
}
```

**Additions needed:**
```json
{
  "Network": {
    "Bootnodes": "enode://...@bootnode1:30303,enode://...@bootnode2:30303",
    "P2PPort": 30303,
    "DiscoveryPort": 30303
  }
}
```

### 8.3 Network ID

XDC Mainnet: **50**  
XDC Devnet (Apothem): **51**

This is already defined in the chainspec genesis config.

### 8.4 Launch Command

```bash
./Nethermind.Runner --config xdc
```

Or with Docker:
```bash
docker run nethermind/nethermind --config xdc
```

---

## 9. Implementation Steps (Ordered)

### Phase 1: Protocol Infrastructure (Foundation)

| # | Task | File(s) | Effort |
|---|------|---------|--------|
| 1.1 | Add `Eth100 = 100` to EthVersions | `Nethermind.Network.Contract/P2P/EthVersions.cs` | S |
| 1.2 | Add `IEth100ProtocolFactory` interface | `Nethermind.Network/P2P/Subprotocols/Eth/V100/` | S |
| 1.3 | **Fix message codes** to align with geth-xdc | `Nethermind.Xdc/P2P/Eth100/Eth100MessageCode.cs` | S |
| 1.4 | **Fix MessageIdSpaceSize** to 227 | `Nethermind.Xdc/P2P/Eth100/Eth100ProtocolHandler.cs` | S |
| 1.5 | Implement `Eth100ProtocolFactory` | `Nethermind.Xdc/P2P/Eth100/Eth100ProtocolFactory.cs` | M |

### Phase 2: Registration & Wiring

| # | Task | File(s) | Effort |
|---|------|---------|--------|
| 2.1 | Add `IEth100ProtocolFactory?` to ProtocolsManager | `Nethermind.Network/ProtocolsManager.cs` | M |
| 2.2 | Add `case 100:` to protocol factory switch | `Nethermind.Network/ProtocolsManager.cs` | S |
| 2.3 | Register serializers in XdcModule | `Nethermind.Xdc/XdcModule.cs` | S |
| 2.4 | Register `IXdcConsensusMessageProcessor` | `Nethermind.Xdc/XdcModule.cs` | S |
| 2.5 | Register `IEth100ProtocolFactory` | `Nethermind.Xdc/XdcModule.cs` | S |
| 2.6 | Add XDC capability management to XdcPlugin | `Nethermind.Xdc/XdcPlugin.cs` | M |

### Phase 3: Handshake & Sync

| # | Task | File(s) | Effort |
|---|------|---------|--------|
| 3.1 | Verify StatusMessage without ForkID works | Test | S |
| 3.2 | Verify TD calculation for XDC | Test | S |
| 3.3 | Test peer connection with geth-xdc node | Integration | L |
| 3.4 | Test block header sync (standard eth/63 messages) | Integration | L |

### Phase 4: Consensus Message Flow

| # | Task | File(s) | Effort |
|---|------|---------|--------|
| 4.1 | Implement `IXdcBroadcaster` | New file in `Nethermind.Xdc` | M |
| 4.2 | Wire broadcaster to consensus engine | `XdcHotStuff.cs` | M |
| 4.3 | Complete `XdcConsensusMessageProcessor` TODOs | `XdcConsensusMessageProcessor.cs` | L |
| 4.4 | Add vote/timeout signature validation | `XdcConsensusMessageProcessor.cs` | L |

### Phase 5: Full Consensus

| # | Task | File(s) | Effort |
|---|------|---------|--------|
| 5.1 | Implement full vote handling pipeline | `VotesManager.cs` | XL |
| 5.2 | Implement timeout handling pipeline | `TimeoutCertificateManager.cs` | XL |
| 5.3 | Implement SyncInfo processing | `SyncInfoManager.cs` | L |
| 5.4 | End-to-end XDC mainnet sync test | Infrastructure | XL |

**Size Key:** S=Small (<2h), M=Medium (2-8h), L=Large (1-3d), XL=Very Large (>3d)

---

## 10. Testing Strategy

### 10.1 Unit Tests

**Protocol Handler Tests:**
```csharp
// File: Nethermind.Xdc.Test/P2P/Eth100ProtocolHandlerTests.cs

[Test]
public void Should_return_correct_protocol_version()
{
    var handler = CreateHandler();
    Assert.That(handler.ProtocolVersion, Is.EqualTo(100));
}

[Test]
public void Should_return_correct_message_id_space_size()
{
    var handler = CreateHandler();
    Assert.That(handler.MessageIdSpaceSize, Is.EqualTo(227));
}

[Test]
public void Should_handle_vote_message()
{
    var processor = Substitute.For<IXdcConsensusMessageProcessor>();
    var handler = CreateHandler(processor);
    var vote = new Vote(new BlockRoundInfo(Hash256.Zero, 1, 100), 0);
    
    handler.HandleVote(new VoteP2PMessage(vote));
    
    processor.Received(1).ProcessVote(Arg.Any<Vote>());
}

[Test]
public void Should_delegate_unknown_messages_to_base_eth63()
{
    // Messages 0x00-0x10 should be handled by Eth63ProtocolHandler
    // Test with GetBlockHeaders (0x03)
}
```

**Serializer Tests:**
```csharp
// File: Nethermind.Xdc.Test/P2P/Eth100SerializerTests.cs

[Test]
public void Vote_roundtrip_serialization()
{
    var vote = CreateTestVote();
    var serializer = new VoteP2PMessageSerializer();
    var message = new VoteP2PMessage(vote);
    
    var buffer = PooledByteBufferAllocator.Default.Buffer(1024);
    serializer.Serialize(buffer, message);
    
    var deserialized = serializer.Deserialize(buffer);
    Assert.That(deserialized.Vote.ProposedBlockInfo.Round, Is.EqualTo(vote.ProposedBlockInfo.Round));
}

// Similar tests for Timeout, SyncInfo, QuorumCertificate
```

**Factory Tests:**
```csharp
[Test]
public void Factory_creates_eth100_handler()
{
    var factory = new Eth100ProtocolFactory(mockProcessor);
    var handler = factory.Create(session, serializer, stats, syncServer, ...);
    
    Assert.That(handler, Is.InstanceOf<Eth100ProtocolHandler>());
    Assert.That(handler.ProtocolVersion, Is.EqualTo(100));
}
```

### 10.2 Integration Tests

**Handshake Test (Against geth-xdc):**
```
1. Start geth-xdc node in devnet mode
2. Start Nethermind with XDC config
3. Connect via static peer
4. Verify:
   - Capability negotiation selects eth/100
   - Status message exchange succeeds (no ForkID)
   - NetworkId matches (50 or 51)
   - Genesis hash matches
   - Peer is added to sync pool
```

**Block Sync Test:**
```
1. Start geth-xdc with blocks synced to N
2. Start Nethermind fresh
3. Verify:
   - GetBlockHeaders/BlockHeaders work
   - GetBlockBodies/BlockBodies work  
   - GetReceipts/Receipts work
   - Block validation passes (XDC header validation)
   - Chain advances to block N
```

**Consensus Message Test:**
```
1. Start geth-xdc masternode (validator)
2. Start Nethermind as observer
3. Verify:
   - Vote messages received and deserialized
   - Timeout messages received and deserialized
   - SyncInfo messages received and deserialized
   - No crashes or disconnects
```

### 10.3 Cross-Client Compatibility Matrix

| Test Case | Geth-XDC ↔ Nethermind | Nethermind ↔ Nethermind |
|-----------|----------------------|------------------------|
| Handshake (eth/100) | ✅ Must pass | ✅ Must pass |
| Handshake (eth/63) | ✅ Must pass | ✅ Must pass |
| Block sync | ✅ Must pass | ✅ Must pass |
| Vote relay | ✅ Must pass | ✅ Must pass |
| Timeout relay | ✅ Must pass | ✅ Must pass |
| SyncInfo relay | ✅ Must pass | ✅ Must pass |

### 10.4 Test Infrastructure

```bash
# Local devnet setup for testing
# Start geth-xdc:
./geth --networkid 51 --datadir /tmp/xdc-geth --port 30303

# Start Nethermind:
./Nethermind.Runner --config xdc \
  --Network.StaticPeers "enode://...@127.0.0.1:30303"
```

---

## 11. Reference Implementations

### 11.1 geth-xdc Protocol Definition

From `geth-xdc/eth/protocols/eth/protocol.go`:
```go
const XDPOS2 = 100

var ProtocolVersions = []uint{XDPOS2, ETH63, ETH62}

var protocolLengths = map[uint]uint64{
    XDPOS2: 227,  // 0x00-0xe2 (sparse)
}

// Message codes
const (
    VoteMsg     = 0xe0
    TimeoutMsg  = 0xe1
    SyncInfoMsg = 0xe2
)
```

### 11.2 geth-xdc Handshake

From `geth-xdc/eth/protocols/eth/handshake.go`:
```go
// XDC uses eth/62 format without ForkID
func (p *Peer) handshake62(networkID uint64, chain forkid.Blockchain) error {
    td := new(big.Int).SetUint64(latest.Number.Uint64())
    pkt := &StatusPacket62{
        ProtocolVersion: uint32(p.version),  // 100
        NetworkID:       networkID,           // 50
        TD:              td,
        Head:            latest.Hash(),
        Genesis:         genesis.Hash(),
    }
    // No ForkID
}
```

### 11.3 geth-xdc Message Handling

From `geth-xdc/eth/protocols/eth/handler.go`:
```go
var xdpos2 = map[uint64]msgHandler{
    // Standard eth/63 messages
    NewBlockHashesMsg:  handleNewBlockhashes,
    NewBlockMsg:        handleNewBlock,
    TransactionsMsg:    handleTransactions,
    GetBlockHeadersMsg: handleGetBlockHeaders,
    BlockHeadersMsg:    handleBlockHeaders,
    GetBlockBodiesMsg:  handleGetBlockBodies,
    BlockBodiesMsg:     handleBlockBodies,
    GetNodeDataMsg:     handleGetNodeData,
    NodeDataMsg:        handleNodeData,
    GetReceiptsMsg:     handleGetReceipts68,
    ReceiptsMsg:        handleReceipts,
    // XDPoS2 consensus
    VoteMsg:     handleVoteMsg,
    TimeoutMsg:  handleTimeoutMsg,
    SyncInfoMsg: handleSyncInfoMsg,
}
```

---

## 12. Risks & Mitigations

### R1: Message Code Mismatch (HIGH RISK)

**Risk:** Current codes `0x11-0x14` don't match geth-xdc `0xe0-0xe2`.  
**Impact:** Peers would misinterpret messages → disconnections.  
**Mitigation:** Align codes before ANY network testing. This is the #1 priority.

### R2: Missing QuorumCertificate Message in geth-xdc (MEDIUM)

**Risk:** geth-xdc has only 3 consensus messages (Vote, Timeout, SyncInfo). Our Nethermind has 4 (+ QC).  
**Impact:** Nethermind sending standalone QC messages to geth peers → errors/disconnections.  
**Mitigation:** Keep QC message for Nethermind-to-Nethermind, but never send to geth peers. Or remove the standalone QC message and only transmit QCs inside SyncInfo/block headers (matching geth behavior).

### R3: Circular Dependency (LOW)

**Risk:** `Nethermind.Network` needs to know about eth/100 handler which is in `Nethermind.Xdc`.  
**Impact:** Build failure.  
**Mitigation:** Use the `IEth100ProtocolFactory` interface pattern (Section 4.1).

### R4: Sync Mode Incompatibility (MEDIUM)

**Risk:** XDC doesn't support snap sync the same way Ethereum does.  
**Impact:** Sync stalls.  
**Mitigation:** Start with full/fast sync only. Verify snap sync compatibility separately.

### R5: Extra Data / Header Encoding (MEDIUM)

**Risk:** XDC headers have custom extra data (QC, round info). Standard Nethermind header RLP may reject them.  
**Impact:** Block validation failures.  
**Mitigation:** `XdcHeaderStore` and `XdcBlockHeader` already handle custom encoding. Verify roundtrip.

---

## Appendix A: File Change Summary

| File | Change Type | Description |
|------|-------------|-------------|
| `Nethermind.Network.Contract/P2P/EthVersions.cs` | **Modify** | Add `Eth100 = 100` |
| `Nethermind.Network/P2P/Subprotocols/Eth/V100/IEth100ProtocolFactory.cs` | **Create** | Factory interface |
| `Nethermind.Network/ProtocolsManager.cs` | **Modify** | Add `IEth100ProtocolFactory?`, `case 100:` in switch |
| `Nethermind.Xdc/P2P/Eth100/Eth100MessageCode.cs` | **Modify** | Fix codes: 0xe0, 0xe1, 0xe2 |
| `Nethermind.Xdc/P2P/Eth100/Eth100ProtocolHandler.cs` | **Modify** | Fix MessageIdSpaceSize=227, update switch cases |
| `Nethermind.Xdc/P2P/Eth100/Eth100ProtocolFactory.cs` | **Create** | Implements IEth100ProtocolFactory |
| `Nethermind.Xdc/XdcModule.cs` | **Modify** | Register serializers, processor, factory |
| `Nethermind.Xdc/XdcPlugin.cs` | **Modify** | Manage capabilities at startup |
| `Nethermind.Xdc.Test/P2P/Eth100*.cs` | **Create** | Unit tests |

## Appendix B: Dependency Graph

```
Nethermind.Network.Contract  (defines: Protocol, EthVersions, IEth100ProtocolFactory)
        ▲
        │
Nethermind.Network           (defines: ProtocolsManager, serialization infra)
        ▲                    (consumes: IEth100ProtocolFactory)
        │
Nethermind.Init              (defines: NetworkModule - registers standard serializers)
        ▲
        │
Nethermind.Xdc               (implements: Eth100ProtocolFactory, handler, serializers)
        │                    (registers: via XdcModule)
        ▼
Nethermind.Runner            (loads: XdcPlugin when chain spec says XDPoS)
```

## Appendix C: devp2p Capability Negotiation

When two Nethermind-XDC nodes connect:

```
Node A Hello: capabilities = [(eth,62), (eth,63), (eth,100)]
Node B Hello: capabilities = [(eth,62), (eth,63), (eth,100)]

Negotiation:
  - Common: eth/62, eth/63, eth/100
  - Highest common: eth/100 ← selected
  - eth/100 handler activated
  - Message ID offset calculated:
    - p2p: 0-15 (base)
    - eth/100: 16-242 (offset 16, length 227)
```

When Nethermind-XDC connects to geth-xdc:

```
Nethermind Hello: capabilities = [(eth,62), (eth,63), (eth,100)]
geth-xdc Hello: capabilities = [(eth,62), (eth,63), (eth,100)]

Same negotiation → eth/100 selected ✅
```

---

*End of Implementation Plan*
