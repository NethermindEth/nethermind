# Erigon (Geth-XDC) eth/100 Implementation Reference

**Analyzed Repository:** XinFinOrg/XDPoSChain (Geth-based, not Erigon)  
**Analysis Date:** February 16, 2026  
**Purpose:** Reference for porting eth/100 protocol to Nethermind

---

## Executive Summary

XDC's **eth/100** (also called **XDPOS2**) is a custom protocol version that extends Ethereum's eth/63 with XDPoS v2 BFT consensus messages. Key characteristics:

- **Protocol Version:** 100 (constant `XDPOS2`)
- **Message Format:** eth/63-style (no RequestId wrapper like eth/66+)
- **New Messages:** Vote (0xe0), Timeout (0xe1), SyncInfo (0xe2)
- **Backwards Compatible:** Falls back to eth/63, eth/62 for non-XDC nodes

---

## 1. Protocol Version Negotiation

### Protocol Constants

```go
// From: eth/protocols/eth/protocol.go
const (
    ETH62  = 62   // XDC compatible
    ETH63  = 63   // XDC compatible
    ETH66  = 66   // eth/66: introduced RequestId wrapping
    ETH68  = 68
    ETH69  = 69
    XDPOS2 = 100  // XDC custom protocol (uses eth/63 style messages)
)

// XDC advertises these versions (NOT eth68/69)
var ProtocolVersions = []uint{XDPOS2, ETH63, ETH62}

// Protocol message counts
var protocolLengths = map[uint]uint64{
    ETH62:  8,
    ETH63:  17,
    ETH68:  17,
    ETH69:  18,
    XDPOS2: 227,  // Includes all eth/63 + XDPoS2 consensus messages
}
```

### Handshake Process

**eth/100 uses eth/62-style handshake** (no ForkID):

```go
// From: eth/protocols/eth/handshake.go
func (p *Peer) Handshake(networkID uint64, chain forkid.Blockchain, rangeMsg BlockRangeUpdatePacket) error {
    switch p.version {
    case ETH62, ETH63, XDPOS2:
        // XDC uses eth/62, eth/63, and xdpos2 without ForkID
        return p.handshake62(networkID, chain)
    case ETH68:
        return p.handshake68(networkID, chain)
    case ETH69:
        return p.handshake69(networkID, chain, rangeMsg)
    default:
        return errors.New("unsupported protocol version")
    }
}

func (p *Peer) handshake62(networkID uint64, chain forkid.Blockchain) error {
    var (
        genesis = chain.Genesis()
        latest  = chain.CurrentHeader()
    )

    // XDC fix: During sync, use CurrentBlock() if higher than CurrentHeader()
    if xdcChain, ok := chain.(XDCBlockchain); ok {
        if currentBlock := xdcChain.CurrentBlock(); currentBlock != nil {
            if currentBlock.Number.Uint64() > latest.Number.Uint64() {
                log.Debug("XDC handshake: using CurrentBlock instead of CurrentHeader")
                latest = currentBlock
            }
        }
    }

    // XDC is pre-merge, use placeholder TD based on block number
    td := new(big.Int).SetUint64(latest.Number.Uint64())
    
    // Send status packet
    pkt := &StatusPacket62{
        ProtocolVersion: uint32(p.version),
        NetworkID:       networkID,
        TD:              td,
        Head:            latest.Hash(),
        Genesis:         genesis.Hash(),
    }
    // Send and receive status messages...
}

// StatusPacket62 structure (NO ForkID)
type StatusPacket62 struct {
    ProtocolVersion uint32
    NetworkID       uint64
    TD              *big.Int
    Head            common.Hash
    Genesis         common.Hash
}
```

**Key Lessons:**
1. **No ForkID validation** - eth/100 doesn't use fork IDs
2. **Use block number as TD** - TD is not tracked for PoS chains
3. **CurrentBlock vs CurrentHeader** - During sync, CurrentBlock may be ahead

---

## 2. XDPoS v2 Message Structures

### Message Type Definitions

```go
// From: eth/protocols/eth/protocol.go
const (
    // Standard eth/63 messages (0x00-0x10)
    StatusMsg          = 0x00
    NewBlockHashesMsg  = 0x01
    TransactionsMsg    = 0x02
    GetBlockHeadersMsg = 0x03
    BlockHeadersMsg    = 0x04
    GetBlockBodiesMsg  = 0x05
    BlockBodiesMsg     = 0x06
    NewBlockMsg        = 0x07
    GetNodeDataMsg     = 0x0d
    NodeDataMsg        = 0x0e
    GetReceiptsMsg     = 0x0f
    ReceiptsMsg        = 0x10

    // XDPoS2 consensus messages (0xe0-0xe2)
    VoteMsg     = 0xe0
    TimeoutMsg  = 0xe1
    SyncInfoMsg = 0xe2
)
```

### Vote Message

```go
// From: core/types/consensus_v2.go
type Vote struct {
    signer            common.Address // unexported, set via SetSigner
    ProposedBlockInfo *BlockInfo     `json:"proposedBlockInfo"`
    Signature         Signature      `json:"signature"`
    GapNumber         uint64         `json:"gapNumber"`
}

type BlockInfo struct {
    Hash   common.Hash `json:"hash"`
    Round  Round       `json:"round"`
    Number *big.Int    `json:"number"`
}

type Round uint64
type Signature []byte

// Vote hashing for deduplication
func (v *Vote) Hash() common.Hash {
    return rlpHash(v)
}

// Pool key for grouping votes
func (v *Vote) PoolKey() string {
    return fmt.Sprint(v.ProposedBlockInfo.Round, ":", v.GapNumber, ":", 
                     v.ProposedBlockInfo.Number, ":", v.ProposedBlockInfo.Hash.Hex())
}

// Message type implementation
func (*Vote) Name() string { return "Vote" }
func (*Vote) Kind() byte   { return 0xe0 }
```

### Timeout Message

```go
type Timeout struct {
    signer    common.Address
    Round     Round
    Signature Signature
    GapNumber uint64
}

func (t *Timeout) Hash() common.Hash {
    return rlpHash(t)
}

func (t *Timeout) PoolKey() string {
    return fmt.Sprint(t.Round, ":", t.GapNumber)
}

func (*Timeout) Name() string { return "Timeout" }
func (*Timeout) Kind() byte   { return 0xe1 }
```

### SyncInfo Message

```go
type SyncInfo struct {
    HighestQuorumCert  *QuorumCert
    HighestTimeoutCert *TimeoutCert
}

type QuorumCert struct {
    ProposedBlockInfo *BlockInfo  `json:"proposedBlockInfo"`
    Signatures        []Signature `json:"signatures"`
    GapNumber         uint64      `json:"gapNumber"`
}

type TimeoutCert struct {
    Round      Round
    Signatures []Signature
    GapNumber  uint64
}

func (s *SyncInfo) Hash() common.Hash {
    return rlpHash(s)
}

func (*SyncInfo) Name() string { return "SyncInfo" }
func (*SyncInfo) Kind() byte   { return 0xe2 }
```

### Signing Helpers

```go
// Structures used for generating signatures
type VoteForSign struct {
    ProposedBlockInfo *BlockInfo
    GapNumber         uint64
}

func VoteSigHash(m *VoteForSign) common.Hash {
    return rlpHash(m)
}

type TimeoutForSign struct {
    Round     Round
    GapNumber uint64
}

func TimeoutSigHash(m *TimeoutForSign) common.Hash {
    return rlpHash(m)
}
```

**Key Lessons:**
1. **RLP Serialization** - All messages use RLP encoding
2. **Signer is separate** - Recovered from signature, not serialized
3. **PoolKey grouping** - Messages are pooled by round/gap/block
4. **GapNumber** - Checkpoint/epoch number (450 blocks)

---

## 3. Message Format: Legacy vs Modern

### Critical Difference: No RequestId Wrapper

```go
// From: eth/protocols/eth/handlers.go

func handleGetBlockHeaders(backend Backend, msg Decoder, peer *Peer) error {
    version := peer.Version()
    useLegacyFormat := version == ETH62 || version == ETH63 || version == XDPOS2
    
    if !useLegacyFormat && version >= ETH66 {
        // Modern eth/66+ format with RequestId
        var query GetBlockHeadersPacket
        if err := msg.Decode(&query); err != nil {
            return err
        }
        response := ServiceGetBlockHeadersQuery(backend.Chain(), query.GetBlockHeadersRequest, peer)
        return peer.ReplyBlockHeadersRLP(query.RequestId, response)
    }
    
    // Legacy format (eth/63, eth/62, XDC eth/100) - no RequestId wrapper
    var legacyQuery GetBlockHeadersRequest
    if err := msg.Decode(&legacyQuery); err != nil {
        log.Debug("Legacy GetBlockHeaders decode failed", "version", version, "err", err)
        return err
    }
    log.Debug("Using legacy GetBlockHeaders format", "version", version)
    response := ServiceGetBlockHeadersQuery(backend.Chain(), &legacyQuery, peer)
    
    // For legacy protocols, send response WITHOUT RequestId
    return peer.ReplyBlockHeadersRLPLegacy(response)
}
```

### Request/Response Structures

```go
// Modern (eth/66+): WITH RequestId wrapper
type GetBlockHeadersPacket struct {
    RequestId uint64
    *GetBlockHeadersRequest
}

type BlockHeadersPacket struct {
    RequestId uint64
    BlockHeadersRequest
}

// Legacy (eth/100): WITHOUT RequestId wrapper  
type GetBlockHeadersRequest struct {
    Origin  HashOrNumber
    Amount  uint64
    Skip    uint64
    Reverse bool
}

type BlockHeadersRequest []*types.Header
```

### Peer Reply Methods

```go
// From: eth/protocols/eth/peer.go

// Modern: WITH RequestId
func (p *Peer) ReplyBlockHeadersRLP(id uint64, headers []rlp.RawValue) error {
    return p2p.Send(p.rw, BlockHeadersMsg, &BlockHeadersRLPPacket{
        RequestId:               id,
        BlockHeadersRLPResponse: headers,
    })
}

// Legacy: WITHOUT RequestId (used by eth/100)
func (p *Peer) ReplyBlockHeadersRLPLegacy(headers []rlp.RawValue) error {
    return p2p.Send(p.rw, BlockHeadersMsg, headers)
}

// Same pattern for BlockBodies
func (p *Peer) ReplyBlockBodiesRLPLegacy(bodies []rlp.RawValue) error {
    return p2p.Send(p.rw, BlockBodiesMsg, bodies)
}
```

**Key Lessons:**
1. **Version detection is critical** - Check peer version before encoding/decoding
2. **Legacy sync path** - eth/100 messages bypass modern request tracking
3. **Direct backend handling** - Legacy responses go straight to backend, not dispatcher

---

## 4. Protocol Handler Integration

### Handler Registration

```go
// From: eth/protocols/eth/handler.go

// xdpos2 handlers - XDC consensus protocol
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
    ReceiptsMsg:        handleReceipts[*ReceiptList68],
    
    // eth/69 compatibility (some XDC nodes send this)
    BlockRangeUpdateMsg: handleBlockRangeUpdate,
    
    // XDPoS2 consensus messages
    VoteMsg:     handleVoteMsg,
    TimeoutMsg:  handleTimeoutMsg,
    SyncInfoMsg: handleSyncInfoMsg,
}

func handleMessage(backend Backend, peer *Peer) error {
    msg, err := peer.rw.ReadMsg()
    if err != nil {
        return err
    }
    if msg.Size > maxMessageSize {
        return fmt.Errorf("%w: %v > %v", errMsgTooLarge, msg.Size, maxMessageSize)
    }
    defer msg.Discard()

    var handlers map[uint64]msgHandler
    switch peer.version {
    case ETH62:
        handlers = eth62
    case ETH63:
        handlers = eth63
    case XDPOS2:
        handlers = xdpos2
    case ETH68:
        handlers = eth68
    case ETH69:
        handlers = eth69
    default:
        return fmt.Errorf("unknown eth protocol version: %v", peer.version)
    }

    if handler := handlers[msg.Code]; handler != nil {
        return handler(backend, msg, peer)
    }
    return fmt.Errorf("%w: %v", errInvalidMsgCode, msg.Code)
}
```

### BFT Message Handlers

```go
// From: eth/protocols/eth/handlers.go

func handleVoteMsg(backend Backend, msg Decoder, peer *Peer) error {
    // Decode the vote message
    var vote types.Vote
    if err := msg.Decode(&vote); err != nil {
        return fmt.Errorf("failed to decode Vote message: %v", err)
    }
    
    // Pass to backend for BFT processing
    return backend.Handle(peer, &vote)
}

func handleTimeoutMsg(backend Backend, msg Decoder, peer *Peer) error {
    var timeout types.Timeout
    if err := msg.Decode(&timeout); err != nil {
        return fmt.Errorf("failed to decode Timeout message: %v", err)
    }
    
    return backend.Handle(peer, &timeout)
}

func handleSyncInfoMsg(backend Backend, msg Decoder, peer *Peer) error {
    var syncInfo types.SyncInfo
    if err := msg.Decode(&syncInfo); err != nil {
        return fmt.Errorf("failed to decode SyncInfo message: %v", err)
    }
    
    return backend.Handle(peer, &syncInfo)
}
```

**Key Lessons:**
1. **Backend.Handle() integration** - All BFT messages go to consensus engine
2. **Simple decode + forward** - Protocol handler doesn't process consensus logic
3. **Error handling** - Decode errors disconnect the peer

---

## 5. BFT Peer Extensions

### Peer Message Tracking

```go
// From: eth/protocols/eth/peer_bft.go

const (
    maxKnownVotes     = 131072
    maxKnownTimeouts  = 131072
    maxKnownSyncInfos = 131072
)

type BFTPeer struct {
    *Peer
    
    // Known BFT message hashes
    knownVotes     mapset.Set[common.Hash]
    knownTimeouts  mapset.Set[common.Hash]
    knownSyncInfos mapset.Set[common.Hash]
}

func NewBFTPeer(p *Peer) *BFTPeer {
    return &BFTPeer{
        Peer:           p,
        knownVotes:     mapset.NewSet[common.Hash](),
        knownTimeouts:  mapset.NewSet[common.Hash](),
        knownSyncInfos: mapset.NewSet[common.Hash](),
    }
}

// Mark messages as known to prevent re-broadcasting
func (p *BFTPeer) MarkVote(hash common.Hash) {
    for p.knownVotes.Cardinality() >= maxKnownVotes {
        p.knownVotes.Pop()
    }
    p.knownVotes.Add(hash)
}

func (p *BFTPeer) KnownVote(hash common.Hash) bool {
    return p.knownVotes.Contains(hash)
}

// Send methods
func (p *BFTPeer) SendVote(vote *types.Vote) error {
    hash := vote.Hash()
    p.MarkVote(hash)
    return p2p.Send(p.rw, VoteMsg, vote)
}

func (p *BFTPeer) SendTimeout(timeout *types.Timeout) error {
    hash := timeout.Hash()
    p.MarkTimeout(hash)
    return p2p.Send(p.rw, TimeoutMsg, timeout)
}

func (p *BFTPeer) SendSyncInfo(syncInfo *types.SyncInfo) error {
    hash := syncInfo.Hash()
    p.MarkSyncInfo(hash)
    return p2p.Send(p.rw, SyncInfoMsg, syncInfo)
}
```

**Key Lessons:**
1. **Deduplication by hash** - Prevent message loops with known-message tracking
2. **LRU eviction** - Keep bounded memory with Pop() when full
3. **Mark before send** - Record outgoing messages to prevent echoes

---

## 6. Logging and Debugging

### Debug Logging Patterns

```go
// From various handlers

// Protocol version logging
log.Debug("Legacy GetBlockHeaders decode failed", "version", version, "err", err)
log.Debug("Using legacy GetBlockHeaders format", "version", version)
log.Info("XDC: received legacy BlockHeaders", "version", version, "count", len(legacyRes))

// Handshake logging
log.Debug("XDC handshake: using CurrentBlock instead of CurrentHeader",
    "currentBlock", currentBlock.Number.Uint64(),
    "currentHeader", latest.Number.Uint64())

// Sync logging
log.Info("XDC Block synchronisation started")
log.Info("XDC sync starting", "peer", peer.id, "localHead", localHead.Number.Uint64())
log.Info("Remote head found", "number", remoteHeight)
log.Error("Failed to fetch headers", "from", current+1, "err", err)

// Request logging
p.Log().Debug("Fetching batch of headers", "count", amount, "fromnum", origin, 
              "skip", skip, "reverse", reverse)
```

### Metrics

```go
// From: eth/protocols/eth/handler.go

// Track handler execution time
if metrics.Enabled() {
    h := fmt.Sprintf("%s/%s/%d/%#02x", p2p.HandleHistName, ProtocolName, peer.Version(), msg.Code)
    defer func(start time.Time) {
        sampler := func() metrics.Sample {
            return metrics.ResettingSample(
                metrics.NewExpDecaySample(1028, 0.015),
            )
        }
        metrics.GetOrRegisterHistogramLazy(h, nil, sampler).Update(time.Since(start).Microseconds())
    }(time.Now())
}
```

**Key Lessons:**
1. **Version-aware logging** - Include protocol version in all messages
2. **Debug vs Info** - Debug for protocol details, Info for sync milestones
3. **Error context** - Always include relevant IDs/numbers in errors
4. **Performance metrics** - Track message processing time per type

---

## 7. Sync Mechanism Integration

### Legacy Sync Behavior

```go
// From: eth/downloader/xdcsync.go

func (d *Downloader) XDCSync() error {
    peers := d.peers.AllPeers()
    if len(peers) == 0 {
        return errNoPeers
    }

    // Single-threaded sync lock
    if !d.synchronising.CompareAndSwap(false, true) {
        return errBusy
    }
    defer d.synchronising.Store(false)

    // Notify sync start
    d.mux.Post(StartEvent{})
    defer func() {
        d.mux.Post(DoneEvent{d.blockchain.CurrentHeader()})
    }()

    peer := peers[0]
    localHead := d.blockchain.CurrentBlock()
    localHeight := localHead.Number.Uint64()
    
    // Binary search for remote head
    searchHeight := localHeight + 1000000
    low := localHeight
    high := searchHeight
    remoteHeight := localHeight
    
    for low < high {
        mid := (low + high + 1) / 2
        headers, err := d.fetchHeadersByNumber(peer, mid, 1, 0, false)
        if err != nil || len(headers) == 0 {
            high = mid - 1
        } else {
            remoteHeight = mid
            low = mid
            if high - low <= 1000 {
                break
            }
        }
    }
    
    // Sync in batches
    batchSize := 128
    for current < remoteHeight {
        headers, err := d.fetchHeadersByNumber(peer, current+1, batchSize, 0, false)
        // ... process headers and bodies
    }
}
```

**Key Lessons:**
1. **Binary search discovery** - Find remote head without known TD
2. **Batch processing** - 128 headers per request
3. **Event emission** - Notify sync start/end via event bus
4. **Single sync lock** - Prevent concurrent sync attempts

---

## 8. Porting Checklist for Nethermind

### Phase 1: Protocol Layer
- [ ] Add `XDPOS2 = 100` constant to protocol versions
- [ ] Implement StatusPacket62 (without ForkID)
- [ ] Add legacy message format handlers (no RequestId)
- [ ] Create eth100 message handler map

### Phase 2: Message Types
- [ ] Define Vote, Timeout, SyncInfo structs
- [ ] Implement RLP serialization for BFT messages
- [ ] Add BlockInfo, QuorumCert, TimeoutCert types
- [ ] Create message hashing methods

### Phase 3: Peer Management
- [ ] Extend peer with BFT message tracking
- [ ] Implement MarkVote/KnownVote methods
- [ ] Add SendVote/SendTimeout/SendSyncInfo
- [ ] Create deduplication logic (LRU with 131k capacity)

### Phase 4: Handler Integration
- [ ] Route Vote/Timeout/SyncInfo to consensus engine
- [ ] Implement handleVoteMsg/handleTimeoutMsg/handleSyncInfoMsg
- [ ] Add version detection for legacy vs modern format
- [ ] Create legacy request/response paths

### Phase 5: Sync Integration
- [ ] Implement eth/100-aware sync logic
- [ ] Add binary search for remote head discovery
- [ ] Handle legacy BlockHeaders/BlockBodies responses
- [ ] Emit sync events

### Phase 6: Testing & Debugging
- [ ] Add protocol version logging
- [ ] Implement BFT message metrics
- [ ] Test handshake with XDC nodes
- [ ] Verify consensus message propagation

---

## 9. Common Pitfalls & Solutions

### Pitfall 1: RequestId Confusion
**Problem:** Trying to use eth/66+ request tracking with eth/100  
**Solution:** Check `peer.Version() == XDPOS2` and use legacy code paths

### Pitfall 2: ForkID Validation
**Problem:** Expecting ForkID in status packet  
**Solution:** eth/100 uses StatusPacket62 without ForkID

### Pitfall 3: TD Calculation
**Problem:** Looking for real total difficulty  
**Solution:** XDC is PoS, use block number as placeholder TD

### Pitfall 4: Message Duplication
**Problem:** BFT messages flood the network  
**Solution:** Track known message hashes before forwarding

### Pitfall 5: Sync State
**Problem:** CurrentHeader() returns stale data during sync  
**Solution:** Use CurrentBlock() when available (may be ahead)

---

## 10. Example: Complete Message Flow

### Sending a Vote

```go
// 1. Consensus engine creates vote
vote := &types.Vote{
    ProposedBlockInfo: &types.BlockInfo{
        Hash:   blockHash,
        Round:  currentRound,
        Number: blockNumber,
    },
    Signature: signature,
    GapNumber: gapNumber,
}

// 2. Get connected peers with eth/100
peers := backend.GetPeers()
for _, peer := range peers {
    if peer.Version() != XDPOS2 {
        continue // Skip non-XDC peers
    }
    
    bftPeer := NewBFTPeer(peer)
    
    // 3. Check if peer already knows this vote
    voteHash := vote.Hash()
    if bftPeer.KnownVote(voteHash) {
        continue
    }
    
    // 4. Send and mark as known
    if err := bftPeer.SendVote(vote); err != nil {
        log.Error("Failed to send vote", "peer", peer.ID(), "err", err)
        continue
    }
    
    log.Debug("Vote sent", "peer", peer.ID(), "round", vote.ProposedBlockInfo.Round)
}
```

### Receiving a Vote

```go
// 1. Protocol handler receives message 0xe0
func handleVoteMsg(backend Backend, msg Decoder, peer *Peer) error {
    // 2. Decode RLP
    var vote types.Vote
    if err := msg.Decode(&vote); err != nil {
        return fmt.Errorf("failed to decode Vote message: %v", err)
    }
    
    // 3. Recover signer from signature
    signer, err := recoverVoteSigner(&vote)
    if err != nil {
        return fmt.Errorf("invalid vote signature: %v", err)
    }
    vote.SetSigner(signer)
    
    // 4. Mark as known for this peer
    voteHash := vote.Hash()
    bftPeer := NewBFTPeer(peer)
    bftPeer.MarkVote(voteHash)
    
    // 5. Forward to consensus engine
    return backend.Handle(peer, &vote)
}

// 6. Consensus engine processes vote
func (c *XDPoS) HandleVote(vote *types.Vote) error {
    // Validate vote
    if !c.IsValidVote(vote) {
        return errInvalidVote
    }
    
    // Add to vote pool
    poolKey := vote.PoolKey()
    c.votePool.Add(poolKey, vote)
    
    // Check if we have 2/3 votes (quorum)
    if c.votePool.HasQuorum(poolKey) {
        qc := c.votePool.BuildQuorumCert(poolKey)
        c.OnQuorumCertificate(qc)
    }
    
    return nil
}
```

---

## 11. Key Differences from Standard Ethereum

| Aspect | Standard Ethereum (eth/68+) | XDC eth/100 |
|--------|---------------------------|-------------|
| **Protocol Version** | 68, 69 | 100 (XDPOS2) |
| **RequestId Wrapper** | Yes (since eth/66) | No (eth/63 style) |
| **ForkID** | Required in status | Not used |
| **Consensus** | Proof of Stake (Beacon) | XDPoS v2 (BFT) |
| **Total Difficulty** | Real PoW/PoS TD | Placeholder (block number) |
| **Extra Messages** | None | Vote, Timeout, SyncInfo |
| **Message Count** | 17-18 | 227 (includes BFT) |
| **Sync Method** | Modern snap/state sync | Legacy header+body sync |

---

## 12. Reference Files

### Primary Sources (Geth-XDC)
- `eth/protocols/eth/protocol.go` - Protocol constants and message types
- `eth/protocols/eth/handshake.go` - eth/100 handshake logic
- `eth/protocols/eth/handlers.go` - Message handlers with legacy format support
- `eth/protocols/eth/handler.go` - Protocol dispatcher and handler registration
- `eth/protocols/eth/peer.go` - Peer methods for sending messages
- `eth/protocols/eth/peer_bft.go` - BFT peer extensions
- `core/types/consensus_v2.go` - XDPoS v2 message structures
- `eth/downloader/xdcsync.go` - Legacy sync implementation

### Testing Resources
- `eth/protocols/eth/protocol_test.go` - Message encoding tests
- `eth/protocols/eth/handler_test.go` - Handler tests
- `eth/protocols/eth/handshake_test.go` - Handshake tests

---

## 13. Summary & Recommendations

### What Makes eth/100 Special
1. **Hybrid Protocol** - Standard Ethereum sync + BFT consensus messages
2. **Legacy Format** - Uses eth/63 message encoding (no RequestId)
3. **BFT Layer** - Vote/Timeout/SyncInfo messages for consensus
4. **Backward Compatible** - Falls back to eth/63, eth/62 gracefully

### Implementation Strategy for Nethermind
1. **Start with protocol layer** - Add XDPOS2 version and handlers
2. **Implement message types** - Focus on correct RLP serialization
3. **Add peer tracking** - Prevent message duplication early
4. **Integrate with consensus** - Route BFT messages to XDPoS engine
5. **Test incrementally** - Handshake → sync → consensus messages

### Critical Success Factors
- **Version detection** - Always check peer version before encoding/decoding
- **Legacy compatibility** - Don't assume RequestId exists
- **Message deduplication** - Essential for BFT message propagation
- **Error handling** - Invalid BFT messages should disconnect peer
- **Logging** - Include version in all protocol-related logs

### Performance Considerations
- **Known message sets** - Limit to 131k entries (LRU eviction)
- **Batch size** - 128 headers per sync request
- **Message size** - Max 10MB per message
- **Timeout** - 5 seconds for handshake

---

**End of Reference Document**

Generated: February 16, 2026  
Source: XinFinOrg/XDPoSChain (Geth-based)  
For: Nethermind eth/100 implementation
