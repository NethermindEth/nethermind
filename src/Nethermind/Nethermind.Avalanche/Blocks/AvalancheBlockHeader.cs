// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Avalanche.Blocks;

/// <summary>
/// An Avalanche C-Chain (Coreth) block header: a vanilla Ethereum <see cref="BlockHeader"/> extended with the
/// three Coreth-specific header fields <see cref="ExtDataHash"/>, <see cref="ExtDataGasUsed"/> and
/// <see cref="BlockGasCost"/>.
/// </summary>
/// <remarks>
/// Coreth defines its header in <c>core/types/block.go</c> (with the extension fields living in
/// <c>plugin/evm/customtypes</c>). The struct field order, ported verbatim, is:
/// <list type="number">
///   <item>ParentHash, UncleHash, Coinbase, Root, TxHash, ReceiptHash, Bloom, Difficulty, Number, GasLimit,
///   GasUsed, Time, Extra, MixDigest, Nonce — byte-for-byte identical to go-ethereum.</item>
///   <item><see cref="ExtDataHash"/> — <c>gencodec:"required"</c>, present in every block since genesis (it is
///   <b>not</b> a Go <c>rlp:"optional"</c> field).</item>
///   <item><see cref="BlockHeader.BaseFeePerGas"/> — <c>rlp:"optional"</c>, from Apricot Phase 3.</item>
///   <item><see cref="ExtDataGasUsed"/> — <c>rlp:"optional"</c>, from Apricot Phase 4.</item>
///   <item><see cref="BlockGasCost"/> — <c>rlp:"optional"</c>, from Apricot Phase 4.</item>
///   <item><see cref="BlockHeader.BlobGasUsed"/>, <see cref="BlockHeader.ExcessBlobGas"/>,
///   <see cref="BlockHeader.ParentBeaconBlockRoot"/> — <c>rlp:"optional"</c>, Cancun-era, usually absent on
///   the C-Chain.</item>
/// </list>
/// Subclassing <see cref="BlockHeader"/> (rather than wrapping one) is the cleaner choice here: every standard
/// Ethereum header field — including the public <see cref="BlockHeader.Difficulty"/> and
/// <see cref="BlockHeader.BaseFeePerGas"/> fields and the <see cref="BlockHeader.Hash"/> property — is reused
/// as-is, and the type flows through any Nethermind code path that consumes a <see cref="BlockHeader"/>. Only
/// the three Avalanche-specific fields are added on top.
/// </remarks>
public class AvalancheBlockHeader(
    Hash256 parentHash,
    Hash256 unclesHash,
    Address beneficiary,
    UInt256 difficulty,
    ulong number,
    ulong gasLimit,
    ulong timestamp,
    byte[] extraData)
    : BlockHeader(parentHash, unclesHash, beneficiary, difficulty, number, gasLimit, timestamp, extraData)
{

    /// <summary>
    /// The Coreth <c>ExtDataHash</c>: <c>keccak256(RLP(extData))</c> committing to the block's atomic-transaction
    /// bytes. Present in every C-Chain block since genesis; equals <see cref="Parity.AvalancheExtData.EmptyExtDataHash"/>
    /// when the block carries no atomic data.
    /// </summary>
    public Hash256? ExtDataHash { get; set; }

    /// <summary>
    /// The Coreth <c>ExtDataGasUsed</c>: gas attributed to atomic-transaction processing. Introduced in Apricot
    /// Phase 4; <c>null</c> for pre-AP4 headers where the field is absent from the RLP.
    /// </summary>
    public UInt256? ExtDataGasUsed { get; set; }

    /// <summary>
    /// The Coreth <c>BlockGasCost</c>: the dynamic-fee block gas cost. Introduced in Apricot Phase 4;
    /// <c>null</c> for pre-AP4 headers where the field is absent from the RLP.
    /// </summary>
    public UInt256? BlockGasCost { get; set; }

    /// <summary>
    /// The Coreth <c>TimeMilliseconds</c>: the block timestamp in milliseconds. Introduced in Granite
    /// (ACP-226); <c>null</c> for pre-Granite headers where the field is absent from the RLP.
    /// </summary>
    public ulong? TimeMilliseconds { get; set; }

    /// <summary>
    /// The Coreth <c>MinDelayExcess</c> (an <c>acp226.DelayExcess</c>): the minimum block-delay excess used by
    /// ACP-226 delay verification. Introduced in Granite; <c>null</c> for pre-Granite headers where the field is
    /// absent from the RLP.
    /// </summary>
    public ulong? MinDelayExcess { get; set; }
}
