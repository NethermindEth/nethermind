<!--
SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
SPDX-License-Identifier: LGPL-3.0-only
-->

# Avalanche C-Chain (Coreth) block & header RLP codec

Decodes/encodes and hashes real Avalanche C-Chain blocks, matching `ava-labs/coreth`
(`core/types/block.go` + `plugin/evm/customtypes`).

- `AvalancheBlockHeader` — `BlockHeader` subclass adding `ExtDataHash`, `ExtDataGasUsed`, `BlockGasCost`.
- `AvalancheHeaderDecoder` — header RLP and `ComputeHash` (= `keccak256(RLP(header))` = the block hash).
- `AvalancheBlock` / `AvalancheBlockBody` / `AvalancheBlockDecoder` — the `extblock`
  `[Header, Txs, Uncles, Version, ExtData]` and body `[Txs, Uncles, Version, ExtData]`.

Header field order (struct order): the 15 go-ethereum fields, then the always-present `ExtDataHash`
(`gencodec:"required"`), then the `rlp:"optional"` tail `BaseFee, ExtDataGasUsed, BlockGasCost, BlobGasUsed,
ExcessBlobGas, ParentBeaconRoot`. `ExtDataHash` must equal `AvalancheExtData.CalcExtDataHash(extData)`.

## Pending: byte-exact mainnet validation

Round-trip and structural correctness are covered by `Nethermind.Avalanche.Test/Blocks`. The final
acceptance check — fetch a real mainnet C-Chain block via `debug_getRawBlock`, decode it, and assert the
decoded `Header.Hash` equals the node's reported block hash — is **pending the C-Chain RPC coming online**.

## Known gap: Granite-era optional fields

Current Coreth master appends two further `rlp:"optional"` fields after `ParentBeaconRoot`:
`TimeMilliseconds` (`*uint64`) and `MinDelayExcess`. This codec targets the pre-Granite shape and does not
yet decode them; extend the optional cascade in `AvalancheHeaderDecoder` (and `AvalancheBlockHeader`) to add
them, preserving field order.
