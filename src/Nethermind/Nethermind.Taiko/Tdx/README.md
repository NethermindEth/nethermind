# TDX Attestation for Taiko Preconfirmed Blocks

This module provides TDX (Trust Domain Extensions) attestation support for Taiko preconfirmed blocks within the Nethermind client. It enables ENS/CCIP to verify L2 state before blocks are proposed or finalized on L1.

## Overview

Preconfirmed blocks exist only in memory and haven't been posted to L1 yet. This module allows generating hardware-backed attestations for these blocks, providing cryptographic proof that:

- The block is canonical
- The state is valid at that moment
- The attestation originates from a registered TDX instance

## Configuration

TDX uses two configuration sections:
- `Surge.TdxEnabled` - Master switch to enable/disable TDX attestation
- `SurgeTdx.*` - TDX-specific settings (socket path, config path, instance ID)

Enable TDX attestation in your Nethermind configuration:

```json
{
  "Surge": {
    "L1EthApiEndpoint": "https://rpc.hoodi.ethpandaops.io/",
    "TaikoInboxAddress": "0x624ec8F33DA83707f360D6d25136AA0741713BC4",
    "TdxEnabled": true
  },
  "SurgeTdx": {
    "SocketPath": "/var/run/tdxs.sock",
    "ConfigPath": "~/.config/nethermind/tdx",
    "InstanceId": 0
  }
}
```

Or via command line:
```bash
nethermind --Surge.TdxEnabled=true --SurgeTdx.SocketPath=/var/run/tdxs.sock
```

| Section | Setting | Default | Description |
|---------|---------|---------|-------------|
| `Surge` | `TdxEnabled` | `false` | Enable TDX attestation support |
| `SurgeTdx` | `SocketPath` | `/var/tdxs.sock` | Path to the `tdxs` daemon Unix socket |
| `SurgeTdx` | `ConfigPath` | `~/.config/nethermind/tdx` | Directory for TDX bootstrap data (keys, quotes) |
| `SurgeTdx` | `InstanceId` | `0` | On-chain registered instance ID (set after registering) |

## Prerequisites

1. **TDX Environment**: Must run inside a TDX-enabled VM
2. **tdxs Daemon**: The `tdxs` abstraction daemon must be running and accessible via Unix socket
3. **Bootstrap**: The TDX service must be bootstrapped before generating attestations

## RPC Endpoints

### `taiko_tdxBootstrap`

Initializes the TDX service by generating a private key and obtaining an initial quote.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "taiko_tdxBootstrap",
  "params": [],
  "id": 1
}
```

**Response (Success):**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "issuerType": "azure",
    "publicKey": "0x742d35cc6634c0532925a3b844bc454e4438f44e",
    "quote": "0x04000200810000000000...",
    "nonce": "0xa1b2c3d4e5f6...",
    "metadata": null
  }
}
```

**Response (TDX Disabled):**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32000,
    "message": "TDX is not enabled. Set Surge.TdxEnabled=true in configuration."
  }
}
```

---

### `taiko_getTdxGuestInfo`

Returns the TDX guest information for on-chain instance registration.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "taiko_getTdxGuestInfo",
  "params": [],
  "id": 1
}
```

**Response (Success):**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "issuerType": "azure",
    "publicKey": "0x742d35cc6634c0532925a3b844bc454e4438f44e",
    "quote": "0x04000200810000000000...",
    "nonce": "0xa1b2c3d4e5f6...",
    "metadata": null
  }
}
```

**Response (Not Bootstrapped):**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32000,
    "message": "TDX service not bootstrapped. Call taiko_tdxBootstrap first."
  }
}
```

---

### `taiko_getTdxAttestation`

Generates a TDX attestation for a specific block.

**Request:**
```json
{
  "jsonrpc": "2.0",
  "method": "taiko_getTdxAttestation",
  "params": ["0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef"],
  "id": 1
}
```

**Response (Success):**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "proof": "0x00000000742d35cc6634c0532925a3b844bc454e4438f44e...",
    "quote": "0x04000200810000000000...",
    "block": {
      "hash": "0x...",
      "parentHash": "0x...",
      "sha3Uncles": "0x...",
      "miner": "0x...",
      "stateRoot": "0x...",
      "transactionsRoot": "0x...",
      "receiptsRoot": "0x...",
      "logsBloom": "0x...",
      "difficulty": "0x0",
      "number": "0x3039",
      "gasLimit": "0x...",
      "gasUsed": "0x...",
      "timestamp": "0x...",
      "extraData": "0x...",
      "mixHash": "0x...",
      "nonce": "0x...",
      "baseFeePerGas": "0x...",
      "withdrawalsRoot": "0x..."
    }
  }
}
```

**Response (Block Not Found):**
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32000,
    "message": "Block not found"
  }
}
```

## Attestation Structure

### What Gets Attested

The attestation is directly over the **block header hash**. This is the simplest and most direct approach:

```
userData = header.Hash   // The block header hash (32 bytes)
signature = sign(userData)
quote = TDX_quote(userData)
```

The response includes the full `BlockHeader`, allowing verifiers to:
1. Compute `keccak256(RLP(header))` to get the header hash
2. Verify the signature is over that hash
3. Verify the TDX quote contains that hash
4. Extract `stateRoot` from the header for their use (e.g., ENS CCIP queries)

### Proof Format (89 bytes)

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| 0 | 4 | `instance_id` | On-chain registered instance ID (big-endian) |
| 4 | 20 | `address` | TDX instance's Ethereum address |
| 24 | 65 | `signature` | ECDSA signature over `header.Hash` (r: 32, s: 32, v: 1) |

### TDX Quote

The quote is obtained from the `tdxs` daemon with `header.Hash` embedded as user data. This cryptographically binds the TDX attestation to the specific block.

## Usage Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                         BOOTSTRAP PHASE                          │
├─────────────────────────────────────────────────────────────────┤
│  1. Start Nethermind with Surge.TdxEnabled=true                  │
│  2. Call taiko_tdxBootstrap                                      │
│  3. Register instance on-chain using returned guestInfo          │
│  4. Update config with assigned SurgeTdx.InstanceId              │
│  5. Restart Nethermind (or apply config reload if supported)     │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                       ATTESTATION PHASE                          │
├─────────────────────────────────────────────────────────────────┤
│  1. Preconfirmed block arrives via Catalyst                      │
│  2. ENS/CCIP calls taiko_getTdxAttestation(blockHash)           │
│  3. NMC signs header.Hash and obtains TDX quote                  │
│  4. Returns proof + quote + full header for verification         │
└─────────────────────────────────────────────────────────────────┘
```

## The tdxs Daemon and Socket Protocol

### What is tdxs?

The `tdxs` daemon is an **external abstraction layer** that wraps Intel TDX hardware attestation. It runs as a separate process in the TDX VM and provides a simple JSON-over-Unix-socket interface. This allows applications to generate TDX quotes without directly interfacing with Intel's low-level APIs.

```
┌──────────────────┐     Unix Socket      ┌──────────────────┐
│   Nethermind     │ ◄──────────────────► │   tdxs daemon    │
│   TdxService     │   JSON protocol      │                  │
└──────────────────┘                      └────────┬─────────┘
                                                   │
                                                   ▼
                                          ┌──────────────────┐
                                          │  Intel TDX HW    │
                                          │  /dev/tdx-guest  │
                                          └──────────────────┘
```

### Socket Protocol

The protocol is simple JSON request/response over a Unix stream socket. Nethermind's implementation matches Raiko's wire protocol exactly.

#### Issue Method (Generate Quote)

Request:
```json
{
  "method": "issue",
  "data": {
    "userData": "0123456789abcdef...",  // 32 bytes hex (header.Hash)
    "nonce": "fedcba9876543210..."       // 32 bytes hex (random)
  }
}
```

Response:
```json
{
  "data": { "document": "04000200810000..." },  // TDX quote bytes as hex
  "error": null
}
```

The `userData` field (the block header hash) is embedded in the TDX quote's `REPORTDATA` (first 32 bytes of the 64-byte field). This cryptographically binds the quote to the specific block being attested.

#### Metadata Method

Request:
```json
{ "method": "metadata", "data": {} }
```

Response:
```json
{
  "data": {
    "issuerType": "azure",  // or "tdx" for bare-metal
    "metadata": { ... }     // Platform-specific info
  },
  "error": null
}
```

#### Connection Handling

1. Connect to Unix socket
2. Send JSON request
3. Shutdown write side (`SHUT_WR`)
4. Read response until EOF
5. Close socket

Each request uses a new connection (no persistent sessions).

## Comparison with Raiko

### Wire Protocol: ✅ Compatible

Both Nethermind and Raiko use identical JSON protocol for communicating with `tdxs`.

### Proof Format: ✅ Compatible

Both produce 89-byte proofs:

| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | instance_id (big-endian) |
| 4 | 20 | address |
| 24 | 65 | signature (r:32, s:32, v:1) |

### What Gets Signed: Different (by design)

| Aspect | Raiko | Nethermind |
|--------|-------|------------|
| **Use Case** | On-chain proving of proposed L2 blocks | Preconfirmation attestation for ENS/CCIP |
| **Block Status** | Already proposed on L1 | Not yet on L1 |
| **Signed Data** | Complex `instanceHash` with metaHash from L1 | Simple `header.Hash` |
| **Verifier** | On-chain smart contract | ENS/CCIP resolver (off-chain) |
| **Response** | Returns `instanceHash` | Returns full block header (via `BlockForRpc`) |

### Design Rationale

Nethermind's preconfirmation attestation uses a simpler approach:

1. **No L1 data required**: Preconfirmed blocks haven't been proposed yet, so there's no `metaHash` from L1's `BlockProposed` event
2. **Self-contained response**: The full header is returned, allowing verifiers to extract `stateRoot` and verify the attestation independently
3. **Direct attestation**: Signing the header hash directly is simpler, more auditable, and matches the original spec

This is intentionally different from Raiko because they serve different purposes:
- **Raiko**: Proves blocks for on-chain ZK verification
- **Nethermind**: Attests preconfirmed blocks for ENS/CCIP queries

## Open Problems / TODOs

### Important

1. **Preconf Block Validation**
   - Currently attests to any block
   - Should we restrict to only preconfirmed (non-proposed) blocks?

2. **Async Operations**
   - `TdxsClient` uses synchronous socket operations
   - Consider async for high-volume scenarios

3. **Error Recovery**
   - No retry logic if `tdxs` daemon is temporarily unavailable
   - Consider circuit breaker pattern

### Nice to Have

4. **Metrics**
   - Add Prometheus metrics for attestation latency, success/failure rates

5. **Quote Validation**
   - `ITdxsClient.Validate()` not implemented
   - Could be useful for debugging/self-verification

6. **Key Rotation**
   - No mechanism to rotate TDX instance keys
   - Would require re-registration on-chain

## Security Considerations

### Private Key Storage

The TDX private key is stored with atomic file permissions:

- Key file is created with `0600` permissions atomically using `UnixCreateMode`
- No window of vulnerability between file creation and permission setting (fixes race condition in Raiko)
- TDX only runs on Linux, so Unix-specific APIs are safe to use

### File Locations

```
ConfigPath/
├── bootstrap.json      # Metadata (issuer type, public key, quote, nonce)
└── secrets/
    └── priv.key        # Private key (32 bytes, 0600 permissions)
```

### Other Considerations

- The `tdxs` socket should only be accessible to the Nethermind process
- Instance registration on-chain binds the public key to a TDX measurement
- The TDX VM itself provides isolation - external access to the key file requires VM compromise

## Dependencies

- **tdxs daemon**: External service for TDX quote issuance (must be running in TDX VM)
- **TDX hardware**: Intel TDX-enabled CPU and VM

## Testing

Run TDX-specific tests:

```bash
dotnet test --project Nethermind.Taiko.Test --filter "FullyQualifiedName~Tdx"
```

## References

- [Raiko TDX Implementation](https://github.com/taikoxyz/raiko)
- [Intel TDX Documentation](https://www.intel.com/content/www/us/en/developer/tools/trust-domain-extensions/overview.html)
- [Automata DCAP Attestation](https://docs.ata.network/dcap-attestation)


