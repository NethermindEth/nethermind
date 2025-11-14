# XDC Module Architecture Overview

## Table of Contents
1. [Introduction](#introduction)
2. [High-Level Architecture](#high-level-architecture)
3. [Core Components](#core-components)
4. [Consensus Flow](#consensus-flow)
5. [Component Interactions](#component-interactions)
6. [Data Structures](#data-structures)
7. [Key Algorithms](#key-algorithms)

---

## Introduction

The Nethermind XDC module implements a Byzantine Fault Tolerant (BFT) consensus mechanism based on the HotStuff protocol. It extends Ethereum's blockchain architecture with additional consensus features including:

- **HotStuff-based consensus** with 3-chain finalization
- **Quorum Certificates (QC)** for block validation
- **Timeout Certificates (TC)** for liveness
- **Epoch-based validator rotation**
- **Round-robin leader selection**

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         XDC CONSENSUS LAYER                         │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌───────────────┐         ┌──────────────────┐                     │
│  │  XdcHotStuff  │────────▶│ XdcBlockProducer │                     │
│  │ (Orchestrator)│         └──────────────────┘                     │
│  └───────┬───────┘                   │                              │
│          │                           │                              │
│          │                           ▼                              │
│          │                  ┌──────────────────┐                    │
│          │                  │   XdcSealer      │                    │
│          │                  └──────────────────┘                    │
│          │                                                          │
│          ▼                                                          │
│  ┌───────────────────────────────────────────┐                      │
│  │     XdcConsensusContext (State)           │                      │
│  ├───────────────────────────────────────────┤                      │
│  │ - CurrentRound                            │                      │
│  │ - HighestQC / LockQC                      │                      │
│  │ - HighestTC                               │                      │
│  │ - HighestCommitBlock                      │                      │
│  └───────────────┬───────────────────────────┘                      │
│                  │                                                  │
│                  ▼                                                  │
│  ┌──────────────────────────────────────────┐                       │
│  │        Consensus Managers                │                       │
│  ├──────────────────────────────────────────┤                       │
│  │ • QuorumCertificateManager               │                       │
│  │ • VotesManager                           │                       │
│  │ • TimeoutCertificateManager              │                       │
│  │ • EpochSwitchManager                     │                       │
│  │ • SnapshotManager                        │                       │
│  └──────────────────────────────────────────┘                       │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      BLOCKCHAIN LAYER                               │
├─────────────────────────────────────────────────────────────────────┤
│  XdcBlockTree  │  XdcHeaderStore  │  XdcBlockStore                  │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Core Components

### 1. XdcHotStuff (Consensus Orchestrator)

**Purpose**: Main consensus loop coordinator

**Key Responsibilities**:
- Round management
- Leader election
- Block proposal triggering
- Vote coordination
- Timeout handling

**Key Methods**:
```csharp
Task RunRoundChecks(CancellationToken ct)
Task BuildAndProposeBlock(...)
Task CommitCertificateAndVote(...)
Address GetLeaderAddress(...)
```

**State Machine**:
```
     ┌──────────────────────┐
     │   Initialize Round   │
     └──────────┬───────────┘
                │
                ▼
     ┌──────────────────────┐
     │  Am I the leader?    │
     └──────┬────────┬──────┘
            │        │
         Yes│        │No
            │        │
            ▼        ▼
     ┌──────────┐  ┌──────────────┐
     │  Propose │  │  Wait & Vote │
     │  Block   │  │   on Block   │
     └──────────┘  └──────────────┘
            │              │
            └──────┬───────┘
                   ▼
     ┌──────────────────────┐
     │  QC Threshold Met?   │
     └──────┬───────────────┘
            │
            ▼
     ┌──────────────────────┐
     │   Advance Round      │
     └──────────────────────┘
```

---

### 2. XdcConsensusContext (State Manager)

**Purpose**: Central state container for consensus

**State Variables**:
```csharp
ulong CurrentRound              // Current consensus round
QuorumCertificate HighestQC     // Highest known QC
QuorumCertificate LockQC        // Locked QC (safety)
TimeoutCertificate HighestTC    // Highest timeout certificate
BlockRoundInfo HighestCommitBlock // Finalized block
int TimeoutCounter              // Consecutive timeouts
DateTime RoundStarted           // Round start time
```

**Events**:
- `NewRoundSetEvent` - Triggered when advancing to new round

---

### 3. QuorumCertificateManager

**Purpose**: QC verification and commitment

**Key Operations**:

```
┌─────────────────────────────────────────┐
│     QuorumCertificate Lifecycle         │
├─────────────────────────────────────────┤
│                                         │
│  1. Receive QC from block header        │
│           │                             │
│           ▼                             │
│  2. VerifyCertificate()                 │
│     ├─ Check signature threshold        │
│     ├─ Verify each signature            │
│     ├─ Validate gap number              │
│     └─ Match block info                 │
│           │                             │
│           ▼                             │
│  3. CommitCertificate()                 │
│     ├─ Update HighestQC                 │
│     ├─ Update LockQC                    │
│     ├─ Check 3-chain rule               │
│     └─ Finalize grandparent             │
│           │                             │
│           ▼                             │
│  4. Advance Round                       │
│                                         │
└─────────────────────────────────────────┘
```

**3-Chain Finalization Rule**:
```
Block N-2 (Finalized)  ←─  Block N-1  ←─  Block N (Current)
    QC(N-2)                 QC(N-1)         QC(N)
    
Finalization requires:
- 3 consecutive blocks
- 3 consecutive rounds (no gaps)
- Valid QC chain
```

---

### 4. VotesManager

**Purpose**: Vote collection and QC assembly

**Vote Processing Flow**:

```
┌────────────────────────────────────────────────────┐
│              Vote Processing Pipeline              │
├────────────────────────────────────────────────────┤
│                                                    │
│  OnReceiveVote(vote)                               │
│      │                                             │
│      ├─▶ FilterVote()                              │
│      │    ├─ Check round                           │
│      │    ├─ Verify signature                      │
│      │    └─ Check if signer in committee          │
│      │                                             │
│      ├─▶ Add to XdcPool<Vote>                      │
│      │                                             │
│      ├─▶ Check threshold                           │
│      │    (votes >= masternodes * certThreshold)   │
│      │                                             │
│      └─▶ OnVotePoolThresholdReached()              │
│           │                                        │
│           ├─▶ Get valid signatures                 │
│           ├─▶ Create QuorumCertificate             │
│           └─▶ CommitCertificate()                  │
│                                                    │
└────────────────────────────────────────────────────┘
```

**Voting Rules** (from `VerifyVotingRules`):
```
Can vote if ALL of:
1. CurrentRound > HighestVotedRound (no double voting)
2. Block.Round == CurrentRound (right round)
3. LockQC is null OR
   - Block.ParentQC.Round > LockQC.Round OR
   - Block extends from LockQC ancestor
```

---

### 5. EpochSwitchManager

**Purpose**: Manage validator set transitions at epoch boundaries

**Epoch Structure**:
```
Epoch N                    Epoch N+1
├────────────────────┤     ├────────────────────┤
│                    │     │                    │
│  Regular Blocks    │     │  Regular Blocks    │
│  (EpochLength-Gap) │     │                    │
│                    │     │                    │
├────────────────────┤     │                    │
│                    │     │                    │
│  Gap (Snapshot)    │────▶│  New Validators    │
│  Block N*EpochLen  │     │  Applied Here      │
│  -Gap              │     │                    │
│                    │     │                    │
└────────────────────┘     └────────────────────┘
```

**Epoch Switch Detection**:
```csharp
IsEpochSwitchAtBlock(header):
  - Is it the switch block? → TRUE
  - parentRound < epochStartRound? → TRUE
  - Otherwise → FALSE

epochStartRound = round - (round % EpochLength)
```

---

### 6. SnapshotManager

**Purpose**: Store and retrieve validator snapshots

**Snapshot Data**:
```csharp
class Snapshot {
    long BlockNumber           // Snapshot block
    Hash256 HeaderHash         // Block hash
    Address[] NextEpochCandidates // Validator candidates
}
```

**Snapshot Storage**:
```
Block Number           Snapshot                    Usage
─────────────────────────────────────────────────────────
0 (Genesis)    →  GenesisMasterNodes      →  Epoch 0-899
900-Gap=850    →  Candidates from block   →  Epoch 900-1799
1800-Gap=1750  →  Candidates from block   →  Epoch 1800-2699
...
```

---

### 7. TimeoutCertificateManager

**Purpose**: Handle timeouts and ensure liveness

**Timeout Flow**:
```
Node N                      Pool                   Threshold Met
   │                         │                          │
   │  OnCountdownTimer()     │                          │
   ├────────────────────────▶│                          │
   │  SendTimeout(round)     │                          │
   │                         │                          │
   │                         │  Collect timeouts        │
   │                         │  for same round          │
   │                         │                          │
   │                         │  Count >= threshold?     │
   │                         ├─────────────────────────▶│
   │                         │                          │
   │                         │              Create TimeoutCertificate
   │                         │                          │
   │◀────────────────────────┴──────────────────────────┤
   │        Broadcast TC & SyncInfo                     │
   │                                                    │
   │  ProcessTimeoutCertificate()                       │
   ├─ Update HighestTC                                  │
   ├─ Advance to TC.Round + 1                           │
   └─ Reset timeout counter                             │
```

---

## Consensus Flow

### Complete Round Lifecycle

```
┌──────────────────────────────────────────────────────────────┐
│                    ROUND N LIFECYCLE                         │
└──────────────────────────────────────────────────────────────┘

Phase 1: INITIALIZATION
────────────────────────
┌─────────────────┐
│ SetNewRound(N)  │
│ - Reset timeout │
│ - Clear state   │
└────────┬────────┘
         │
         ▼

Phase 2: LEADER SELECTION
──────────────────────────
┌──────────────────────────────┐
│ leaderIndex =                │
│   (round % epoch) % nodeCount│
└──────────┬───────────────────┘
           │
           ▼
    ┌──────────────┐
    │  Am I leader?│
    └──┬────────┬──┘
       │        │
    YES│        │NO
       │        │
       ▼        ▼

Phase 3a: BLOCK PROPOSAL (Leader)
──────────────────────────────────
┌────────────────────┐       Phase 3b: VOTING (Non-leader)
│ BuildBlock()       │       ────────────────────────────
│ - With HighestQC   │       ┌──────────────────────┐
│ - Seal & Sign      │       │ Receive Block        │
└─────────┬──────────┘       │ Verify:              │
          │                  │  - QC valid          │
          ▼                  │  - Round correct     │
┌────────────────────┐       │  - Voting rules OK   │
│ Broadcast Block    │       └──────────┬───────────┘
└─────────┬──────────┘                  │
          │                             ▼
          │                  ┌──────────────────────┐
          │                  │ CastVote()           │
          │                  │ - Sign vote          │
          │                  │ - Broadcast          │
          │                  └──────────┬───────────┘
          │                             │
          └─────────────┬───────────────┘
                        │
                        ▼

Phase 4: QC AGGREGATION
────────────────────────
┌────────────────────────────────┐
│ Collect Votes in Pool          │
│ Wait for threshold:            │
│  votes >= nodes * certThreshold│
└──────────────┬─────────────────┘
               │
               ▼
┌────────────────────────────────┐
│ Create QuorumCertificate       │
│ - Aggregate signatures         │
│ - Verify all valid             │
└──────────────┬─────────────────┘
               │
               ▼

Phase 5: QC COMMITMENT
───────────────────────
┌────────────────────────────────┐
│ CommitCertificate()            │
│ 1. Update HighestQC            │
│ 2. Check 3-chain rule          │
│ 3. Finalize grandparent?       │
│ 4. Update LockQC               │
└──────────────┬─────────────────┘
               │
               ▼
┌────────────────────────────────┐
│ SetNewRound(N+1)               │
└────────────────────────────────┘

Alternative: TIMEOUT PATH
─────────────────────────
If no QC formed:
  ┌─────────────────┐
  │ Timer expires   │
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │ SendTimeout()   │
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │ Collect TCs     │
  │ Form TC         │
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │ SetNewRound(N+1)│
  └─────────────────┘
```

---

## Component Interactions

### Block Proposal & Validation Pipeline

```
┌──────────────┐
│  XdcHotStuff │  Orchestrator triggers
└──────┬───────┘
       │
       │ 1. BuildAndProposeBlock()
       ▼
┌──────────────────┐
│ XdcBlockProducer │  Creates block structure
└──────┬───────────┘
       │
       │ 2. PrepareBlockHeader()
       │    ├─ Add QuorumCert
       │    ├─ Set round number
       │    └─ Set validators (if epoch switch)
       ▼
┌──────────────────┐
│   XdcSealer      │  Signs block
└──────┬───────────┘
       │
       │ 3. SealBlock()
       │    └─ Create ECDSA signature
       ▼
┌──────────────────┐
│  XdcBlockTree    │  Adds to chain
└──────┬───────────┘
       │
       │ 4. SuggestBlock()
       │    └─ Validate against finalized block
       ▼
┌──────────────────┐
│XdcHeaderValidator│ Validates header
└──────┬───────────┘
       │
       │ 5. Validate()
       │    ├─ Check QC
       │    ├─ Verify seal
       │    └─ Check consensus rules
       ▼
┌──────────────────┐
│ XdcSealValidator │  Validates seal & params
└──────┬───────────┘
       │
       │ 6. ValidateParams()
       │    ├─ Verify leader
       │    ├─ Check round sequence
       │    └─ Validate epoch data
       ▼
┌──────────────────┐
│ XdcBlockProcessor│  Executes transactions
└──────┬───────────┘
       │
       │ 7. Process()
       │    └─ Apply state changes
       ▼
┌──────────────────┐
│  Block Committed │
└──────────────────┘
```

---

### Vote Collection & QC Formation

```
Validator Nodes           VotesManager              QCManager
────────────────         ─────────────             ──────────
Node 1  Node 2  Node 3
  │       │       │
  │  Receives Block
  │       │       │
  ├───────┴───────┤
  │ CastVote()    │
  ├──────────────▶│
  │               │ Add to XdcPool<Vote>
  │               ├─────────────────┐
  │               │                 │
  │               │ votes[round,hash].Add(vote)
  │               │                 │
  │               │◀────────────────┘
  │               │
  │               │ Check threshold
  │               ├─────────────────┐
  │               │                 │
  │               │ if count >= threshold
  │               │     │
  │               │     ▼
  │               │ OnVotePoolThresholdReached()
  │               │     │
  │               │     ├─ GetValidSignatures()
  │               │     ├─ Create QC
  │               │     │
  │               │     ▼
  │               ├────────────────▶│
  │               │  CommitCertificate(qc)
  │               │                 │
  │               │                 ├─ Verify QC
  │               │                 ├─ Update HighestQC
  │               │                 ├─ Check 3-chain
  │               │                 ├─ Finalize blocks
  │               │                 └─ Advance round
  │               │                 │
  │               │◀────────────────┤
  │               │  Round advanced
  │◀──────────────┤
  │ NewRoundEvent │
```

---

## Data Structures

### 1. XdcBlockHeader

Extended Ethereum header with consensus fields:

```csharp
XdcBlockHeader {
    // Standard Ethereum fields
    Hash256 ParentHash
    Address Beneficiary
    Hash256 StateRoot
    long Number
    ulong Timestamp
    ...
    
    // XDC-specific fields
    byte[] Validators      // Validator addresses (epoch switch blocks)
    byte[] Validator       // Block signer signature
    byte[] Penalties       // Penalized validators
    
    // Consensus data (in ExtraData)
    ExtraFieldsV2 {
        ulong BlockRound          // Consensus round
        QuorumCertificate QuorumCert  // Parent block's QC
    }
}
```

---

### 2. QuorumCertificate

Proof of 2f+1 votes for a block:

```csharp
QuorumCertificate {
    BlockRoundInfo ProposedBlockInfo {
        Hash256 Hash           // Block hash
        ulong Round           // Block round
        long BlockNumber      // Block height
    }
    Signature[] Signatures    // Aggregated signatures
    ulong GapNumber          // Snapshot gap block number
}
```

**QC Formation**:
```
Votes (2f+1)  ──────▶  Aggregate Signatures  ──────▶  QC
   Vote₁                                               │
   Vote₂              Sign(Hash(BlockInfo))            │
   Vote₃                    ↓                          │
   ...                  [Sig₁, Sig₂, ...]              │
   Voteₙ                                               │
                                                       │
                            QC included in ────────────┘
                            next block's header
```

---

### 3. Vote

Individual validator vote:

```csharp
Vote {
    BlockRoundInfo ProposedBlockInfo  // Block being voted on
    ulong GapNumber                   // Snapshot reference
    Signature Signature               // Validator's signature
    Address Signer                    // Validator address (recovered)
}
```

**Vote Hash Calculation**:
```
VoteHash = Keccak256(
    RLP([
        BlockInfo{hash, round, number},
        GapNumber
    ])
)
```

---

### 4. TimeoutCertificate

Proof of 2f+1 timeout votes:

```csharp
TimeoutCertificate {
    ulong Round               // Timed-out round
    Signature[] Signatures    // Timeout signatures
    ulong GapNumber          // Snapshot reference
}
```

---

### 5. EpochSwitchInfo

Validator set for an epoch:

```csharp
EpochSwitchInfo {
    Address[] Masternodes              // Active validators
    Address[] StandbyNodes             // Standby validators
    Address[] Penalties                // Penalized validators
    BlockRoundInfo EpochSwitchBlockInfo     // Epoch start block
    BlockRoundInfo EpochSwitchParentBlockInfo  // Previous epoch
}
```

---

## Key Algorithms

### 1. Leader Selection

Round-robin rotation within epoch:

```
GetLeaderAddress(round, header):
  1. Get current masternodes for round
  2. If epoch switch at round:
       Calculate new masternodes
  3. leaderIndex = (round % EpochLength) % masternodes.Length
  4. Return masternodes[leaderIndex]
```

**Example** (EpochLength=900, 5 validators):
```
Round   Leader Index    Leader
────────────────────────────────
0       0 % 5 = 0       Node 0
1       1 % 5 = 1       Node 1
2       2 % 5 = 2       Node 2
...
5       5 % 5 = 0       Node 0
900     0 % 5 = 0       Node 0 (new epoch)
```

---

### 2. 3-Chain Finalization Rule

Commit grandparent when three consecutive rounds exist:

```
CommitBlock(proposedBlock, proposedRound, proposedQC):
  1. parent = proposedBlock.parent
  2. grandparent = parent.parent
  
  3. Check conditions:
     ✓ proposedRound - 1 == parent.round
     ✓ proposedRound - 2 == grandparent.round
     ✓ proposedRound > grandparent.round + 1
     
  4. If all pass:
     HighestCommitBlock = grandparent
     Finalize(grandparent)
```

**Visual**:
```
Round N-2          Round N-1          Round N
   │                  │                  │
   ▼                  ▼                  ▼
┌────────┐        ┌────────┐        ┌────────┐
│Block B │◀───────│Block C │◀───────│Block D │
│QC(B)   │        │QC(C)   │        │QC(D)   │
└────────┘        └────────┘        └────────┘
    ▲
    │
    └─── FINALIZED when Block D commits
```

---

### 3. Voting Safety Rule

Prevent conflicting votes:

```
VerifyVotingRules(block, round, parentQC):
  1. CurrentRound > HighestVotedRound?  ✓ No double voting
  2. block.round == CurrentRound?       ✓ Right round
  3. LockQC safety:
     IF LockQC exists:
        - parentQC.round > LockQC.round  OR
        - block extends LockQC ancestor
     ELSE:
        - Always safe
```

**Lock-based Safety**:
```
Scenario: Node locked on Block A (round 10)

Can vote for Block B (round 15)?
  ├─ B's parent QC.round > 10  ──▶ YES ✓
  └─ B extends from A          ──▶ YES ✓

Can vote for Block C (round 12, different fork)?
  ├─ C's parent QC.round > 10  ──▶ YES
  └─ C extends from A?         ──▶ NO  ✗ REJECT
```

---

### 4. Epoch Transition

Calculate next validator set:

```
CalculateNextEpochMasternodes(blockNumber, parentHash):
  1. Load previous snapshot (gap block)
  2. Get candidates from snapshot
  3. Calculate penalties (forensics)
  4. masterodes = candidates - penalties
  5. Enforce maximum: Take(MaxMasternodes)
  6. Return (masternodes, penalties)
```

**Timeline**:
```
Block Number        Action
─────────────────────────────────────────
0-849              Normal blocks
850 (Gap)          Store snapshot with candidates
851-899            Normal blocks
900 (Epoch switch) Apply new validators from snapshot
901-1749           Use new validator set
```

---

## Security Mechanisms

### 1. Byzantine Fault Tolerance

```
Threshold Requirements:
─────────────────────────
Total Validators: N
Byzantine Tolerance: f
Honest Majority: N ≥ 3f + 1

Quorum Certificate: ⌈N * certThreshold⌉ signatures
Default: certThreshold = 2/3
Minimum: 2f + 1 = ⌈2N/3⌉
```

### 2. Fork Prevention

```
XdcBlockTree.Suggest():
  1. Check if new block builds on finalized chain
  2. Search up to MaxSearchDepth (1024 blocks)
  3. Must find finalized block in ancestry
  4. Reject blocks on dead forks
```

**Fork Resistance**:
```
         Finalized Block
              │
              ├─────────────┐
              │             │ (rejected)
        Main Chain      Dead Fork
              │
          (accepted)
```

### 3. Forensics & Slashing

Equivocation detection:

```
DetectEquivocation(vote, votePool):
  1. For each existing vote in pool:
     IF same round AND same signer:
        IF different blocks:
           ─▶ Equivocation detected!
           ─▶ SendVoteEquivocationProof()
```

---

## Performance Characteristics

### Consensus Latency

```
Block Production Time:
─────────────────────────────────
MinePeriod (default: 2s)           Time between blocks
+ Network Propagation              ~100-500ms
+ Vote Collection                  ~100-500ms
+ QC Formation                     ~10-50ms
────────────────────────────────
Total: ~2.2 - 3.0 seconds/block
```

### Finality

```
Finalization Depth: 3 blocks (3-chain rule)

Time to Finality:
─────────────────
3 blocks × 2s = ~6 seconds
  (with optimal conditions)
  
Plus: Network delays, vote collection
Practical: 8-12 seconds
```

### Throughput

```
Transactions per Block: ~2000-5000 (based on gas limit)
Gas Limit: 84,000,000
Block Time: 2 seconds
────────────────────────────────────
TPS (theoretical): 1000 - 2500 tx/s
TPS (practical):    500 - 1000 tx/s
```

---

## Configuration Parameters

### XdcReleaseSpec

```csharp
EpochLength: 900              // Blocks per epoch
Gap: 50                       // Snapshot before epoch end
SwitchBlock: <configured>     // V2 activation block
MaxMasternodes: 108          // Maximum validators
CertThreshold: 0.67          // 2/3 quorum
TimeoutPeriod: 4000ms        // Round timeout
MinePeriod: 2000ms           // Minimum block time
TimeoutSyncThreshold: 3      // SyncInfo after N timeouts
```

### V2 Dynamic Configuration

Allows runtime parameter adjustments:

```csharp
V2ConfigParams[] {
    {
        SwitchRound: 0,
        MaxMasternodes: 108,
        CertThreshold: 0.67,
        TimeoutPeriod: 4000,
        MinePeriod: 2000
    },
    {
        SwitchRound: 1000000,  // Future upgrade
        MaxMasternodes: 150,
        CertThreshold: 0.70,
        ...
    }
}
```

---

## Module Integration

### Autofac Dependency Injection

```csharp
XdcModule registrations:
─────────────────────────────────────
ISpecProvider          → XdcChainSpecBasedSpecProvider
IBlockTree             → XdcBlockTree
IHeaderStore           → XdcHeaderStore
IBlockStore            → XdcBlockStore
ISealer                → XdcSealer
IHeaderValidator       → XdcHeaderValidator
ISealValidator         → XdcSealValidator
IVotesManager          → VotesManager
IQuorumCertificateManager → QuorumCertificateManager
IEpochSwitchManager    → EpochSwitchManager
ISnapshotManager       → SnapshotManager
IXdcConsensusContext   → XdcConsensusContext
...
```

---

## Critical Paths

### 1. Happy Path (Normal Block Production)

```
Time    Component              Action
──────────────────────────────────────────────────
T+0s    XdcHotStuff           Timer triggers
        └─▶ IsMyTurn?         Check if leader
T+0.1s  XdcBlockProducer      Build block
        ├─▶ PrepareHeader     Add QC, set round
        └─▶ ExecuteTxs        Process transactions
T+0.5s  XdcSealer             Sign block
T+0.6s  XdcBlockTree          Suggest block
T+0.7s  Validator Nodes       Receive block
        └─▶ Validate          Check QC, seal
T+0.9s  VotesManager          Cast votes
T+1.5s  VotesManager          Threshold reached
        └─▶ Create QC         Aggregate signatures
T+1.6s  QCManager             Commit QC
        ├─▶ Update HighestQC
        ├─▶ Check 3-chain
        └─▶ Finalize grandparent
T+1.7s  XdcConsensusContext   SetNewRound(N+1)
```

### 2. Timeout Path (No Block Received)

```
Time    Component              Action
──────────────────────────────────────────────────
T+0s    Round N starts
T+4s    TimeoutTimer          Expires
        └─▶ OnCountdownTimer
T+4.1s  TCManager             SendTimeout
        └─▶ Sign timeout
T+4.5s  TCManager             Collect TCs
T+5s    TCManager             Threshold reached
        └─▶ Create TC
T+5.1s  TCManager             ProcessTC
        └─▶ Update HighestTC
T+5.2s  XdcConsensusContext   SetNewRound(N+1)
```

---

## Testing Considerations

### Unit Test Coverage Areas

1. **Consensus Logic**:
   - Leader selection algorithm
   - Voting rule verification
   - QC verification
   - 3-chain finalization

2. **State Transitions**:
   - Round advancement
   - Lock updates
   - Finalization triggers

3. **Edge Cases**:
   - Epoch boundaries
   - Network partitions
   - Byzantine behavior

### Integration Test Scenarios

```
Test: 3-Chain Finalization
──────────────────────────
Setup: 5 validators, normal operation
Steps:
  1. Produce 3 consecutive blocks
  2. Each block gets 2f+1 votes
  3. Form QC for each
  4. Verify block N-2 finalized

Test: Epoch Transition
──────────────────────
Setup: 5 validators, epoch length = 10
Steps:
  1. Produce blocks 0-9
  2. At block 10-Gap(5), store snapshot
  3. At block 10, switch validators
  4. Verify new committee active

Test: Timeout & Recovery
────────────────────────
Setup: 5 validators, leader crashes
Steps:
  1. Leader fails to propose
  2. Nodes timeout after 4s
  3. Form TC with 2f+1 timeouts
  4. Advance round
  5. New leader proposes
```

---

## Common Issues & Solutions

### Issue 1: Fork Detection Failures

**Problem**: Blocks built on non-finalized forks accepted

**Solution**: `XdcBlockTree.Suggest()` checks ancestry up to finalized block

```csharp
protected override AddBlockResult Suggest(Block block, ...) {
    if (finalizedBlock.BlockNumber >= header.Number)
        return InvalidBlock;
    
    // Search ancestry up to MaxSearchDepth
    for (long i = header.Number; i >= finalized; i--) {
        if (finalizedHash == current.ParentHash)
            return base.Suggest(...);  // Valid
    }
    return InvalidBlock;  // On dead fork
}
```

---

### Issue 2: Double Voting

**Problem**: Node votes twice in same round

**Solution**: `VerifyVotingRules` tracks `_highestVotedRound`

```csharp
public bool VerifyVotingRules(...) {
    if ((long)_ctx.CurrentRound <= _highestVotedRound)
        return false;  // Already voted
    
    // ... other checks
    
    _highestVotedRound = votingRound;  // Update after voting
}
```

---

### Issue 3: Epoch Snapshot Mismatch

**Problem**: Validators don't match expected set

**Solution**: Snapshot stored at Gap block before epoch end

```
Block 850 (Gap):    Store snapshot with candidates
Block 900 (Switch): Load snapshot, calculate masternodes
                    masterodes = snapshot.candidates - penalties
```

---

## Monitoring & Observability

### Key Metrics

```
Consensus Health:
─────────────────
- Current Round Number
- Time Since Last Block
- Finalized Block Height
- QC Aggregation Time
- Vote Pool Size
- Timeout Counter
- Active Validator Count

Performance:
────────────
- Block Production Rate
- Average Block Time
- Finalization Lag
- Vote Collection Latency
- Network Message Rate
```

### Log Events

```csharp
Important log points:
─────────────────────
✓ Round advanced
✓ Block proposed (with hash)
✓ Vote cast
✓ QC formed
✓ Block finalized
✓ Epoch switched
✓ Timeout sent
✓ TC formed
✗ Validation failures
✗ Fork detected
```

---

## Future Enhancements

### 1. Pipelining

Currently sequential: Propose → Vote → QC → Next Round

Future: Overlap rounds for higher throughput

```
Round N:     [Propose] ──▶ [Vote] ──▶ [QC]
Round N+1:              [Propose] ──▶ [Vote] ──▶ [QC]
Round N+2:                         [Propose] ──▶ [Vote] ──▶ [QC]
```

### 2. Optimistic Responsiveness

Fast path when all nodes agree (0 timeouts):
- Reduce MinePeriod
- Immediate voting
- Faster finalization

### 3. Forensics Implementation

Currently stub `PenaltyHandler`:
- Detect double signing
- Slash malicious validators
- Reward honest participants

---

## References

### Academic Papers

1. **HotStuff: BFT Consensus with Linearity and Responsiveness**
   - Yin et al., 2019
   - PODC '19

2. **Practical Byzantine Fault Tolerance**
   - Castro & Liskov, 1999
   - OSDI '99

### XDC Documentation

- XDPoS 2.0 White Paper
- XDC Network GitHub
- Nethermind Documentation

---

## Appendix: Component Checklist

### Essential Components ✓

- [x] XdcHotStuff - Consensus orchestrator
- [x] XdcConsensusContext - State management
- [x] QuorumCertificateManager - QC handling
- [x] VotesManager - Vote aggregation
- [x] TimeoutCertificateManager - Timeout handling
- [x] EpochSwitchManager - Validator rotation
- [x] SnapshotManager - Validator snapshots
- [x] XdcBlockProducer - Block creation
- [x] XdcSealer - Block signing
- [x] XdcBlockTree - Chain management
- [x] XdcHeaderValidator - Header validation
- [x] XdcSealValidator - Seal validation

### TODO Components

- [ ] ForensicsProcessor - Slashing logic
- [ ] PenaltyHandler - Penalty calculation (currently stub)
- [ ] SyncInfoManager - Advanced sync (skeleton)
- [ ] Reward calculator - Block rewards

---

## Quick Reference

### Key Interfaces

```csharp
IBlockProducerRunner  // Main consensus loop
IQuorumCertificateManager  // QC operations
IVotesManager  // Vote handling
IEpochSwitchManager  // Epoch logic
ISnapshotManager  // Validator snapshots
ITimeoutCertificateManager  // Timeout handling
IXdcConsensusContext  // Consensus state
```

### Key Classes

```csharp
XdcHotStuff  // Main orchestrator
XdcConsensusContext  // State container
QuorumCertificateManager  // QC logic
VotesManager  // Vote aggregation
EpochSwitchManager  // Epoch management
SnapshotManager  // Snapshot storage
```

### Key Types

```csharp
XdcBlockHeader  // Extended header
QuorumCertificate  // Aggregated votes
Vote  // Individual vote
TimeoutCertificate  // Timeout proof
EpochSwitchInfo  // Validator set
Snapshot  // Validator candidates
```

---

**End of Document**

*This architecture overview provides a comprehensive guide to understanding the XDC consensus module implementation in Nethermind. For implementation details, refer to the source code in `src/Nethermind/Nethermind.Xdc/`.*
