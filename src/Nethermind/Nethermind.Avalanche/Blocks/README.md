<!--
SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
SPDX-License-Identifier: LGPL-3.0-only
-->

# Avalanche C-Chain (Coreth) block & header RLP codec

Decodes/encodes and hashes real Avalanche C-Chain blocks, matching `ava-labs/coreth`
(`core/types/block.go` + `plugin/evm/customtypes`).

- `AvalancheBlockHeader` — `BlockHeader` subclass adding `ExtDataHash`, `ExtDataGasUsed`, `BlockGasCost`,
  `TimeMilliseconds`, `MinDelayExcess`.
- `AvalancheHeaderDecoder` — header RLP and `ComputeHash` (= `keccak256(RLP(header))` = the block hash).
- `AvalancheBlock` / `AvalancheBlockBody` / `AvalancheBlockDecoder` — the `extblock`
  `[Header, Txs, Uncles, Version, ExtData]` and body `[Txs, Uncles, Version, ExtData]`.

Header field order (struct order): the 15 go-ethereum fields, then the always-present `ExtDataHash`
(`gencodec:"required"`), then the eight `rlp:"optional"` tail fields `BaseFee, ExtDataGasUsed, BlockGasCost,
BlobGasUsed, ExcessBlobGas, ParentBeaconRoot, TimeMilliseconds, MinDelayExcess`. The last two are Granite
(ACP-226) additions (`*uint64` on the wire). `ExtDataHash` must equal `AvalancheExtData.CalcExtDataHash(extData)`.

The Go `rlp:"optional"` cascade is reproduced exactly: a trailing optional is written only if it — or any later
optional — is set, so a Granite block forces the (Avalanche-unused) blob/beacon middle fields to be written as
their zero/empty encoding. Decode is positional and re-encode reproduces identical bytes, so the block hash
round-trips regardless of which middle optionals the producer left nil.

## Pending: byte-exact mainnet validation

Round-trip and structural correctness (including the Granite shape) are covered by
`Nethermind.Avalanche.Test/Blocks`. The final acceptance check — fetch a real mainnet C-Chain block via
`debug_getRawBlock`, decode it, and assert the decoded `Header.Hash` equals the node's reported block hash —
is **pending the C-Chain RPC coming online**.
