// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// AuRa-flavoured <see cref="BlockHeader"/> carrying the <c>step</c> + <c>signature</c>
/// seal that AuRa chains write in place of the Ethash <c>mixHash</c> + <c>nonce</c> pair.
/// </summary>
public sealed class AuRaBlockHeader(
    Hash256? parentHash,
    Hash256? unclesHash,
    Address? beneficiary,
    in UInt256 difficulty,
    ulong number,
    ulong gasLimit,
    ulong timestamp,
    byte[] extraData)
    : BlockHeader(parentHash!, unclesHash!, beneficiary!, in difficulty, number, gasLimit, timestamp, extraData),
      IHashResolver
{
    private static readonly AuRaHeaderDecoder _headerDecoder = new();

    /// <summary>The AuRa step the block was sealed in.</summary>
    public ulong AuRaStep { get; set; }

    /// <summary>The sealer's signature; null until the block is sealed.</summary>
    public byte[]? AuRaSignature { get; set; }

    /// <inheritdoc/>
    public ValueHash256 CalculateHash(RlpBehaviors behaviors = RlpBehaviors.None)
    {
        KeccakRlpWriter writer = new();
        _headerDecoder.Encode(ref writer, this, behaviors);
        return writer.GetValueHash();
    }

    public override BlockHeader CloneForProcessing()
    {
        AuRaBlockHeader clone = new(ParentHash, UnclesHash, Beneficiary, Difficulty, Number, GasLimit, Timestamp, ExtraData)
        {
            AuRaStep = AuRaStep,
            AuRaSignature = AuRaSignature,
        };
        CopyProcessingFields(clone);
        return clone;
    }

    /// <summary>Promote a base header to <see cref="AuRaBlockHeader"/>, copying every field. Returns the input unchanged if it's already an AuRa header.</summary>
    public static AuRaBlockHeader UpgradeFrom(BlockHeader src)
    {
        if (src is AuRaBlockHeader aura) return aura;

        return new AuRaBlockHeader(
            src.ParentHash,
            src.UnclesHash,
            src.Beneficiary,
            in src.Difficulty,
            src.Number,
            src.GasLimit,
            src.Timestamp,
            src.ExtraData)
        {
            Author = src.Author,
            StateRoot = src.StateRoot,
            TxRoot = src.TxRoot,
            ReceiptsRoot = src.ReceiptsRoot,
            Bloom = src.Bloom,
            GasUsed = src.GasUsed,
            MixHash = src.MixHash,
            Nonce = src.Nonce,
            Hash = src.Hash,
            TotalDifficulty = src.TotalDifficulty,
            BaseFeePerGas = src.BaseFeePerGas,
            WithdrawalsRoot = src.WithdrawalsRoot,
            ParentBeaconBlockRoot = src.ParentBeaconBlockRoot,
            RequestsHash = src.RequestsHash,
            BlockAccessListHash = src.BlockAccessListHash,
            RecursiveStark = src.RecursiveStark,
            BlobGasUsed = src.BlobGasUsed,
            ExcessBlobGas = src.ExcessBlobGas,
            SlotNumber = src.SlotNumber,
            IsPostMerge = src.IsPostMerge,
        };
    }
}
