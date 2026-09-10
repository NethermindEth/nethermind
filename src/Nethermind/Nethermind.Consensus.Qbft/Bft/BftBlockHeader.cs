// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// A <see cref="BlockHeader"/> whose hash is the BFT on-chain digest (extra data re-encoded with a
/// zero round and no committed seals) instead of the keccak of the full RLP.
/// </summary>
/// <remarks>
/// The seal shape is the standard <c>mixHash</c> + <c>nonce</c>; only the hash rule differs, which is
/// why the header keeps its <see cref="Codec"/> — an IBFT 2.0 era header of a migrated chain hashes
/// with the IBFT codec. Decoded extra data is cached per <see cref="BlockHeader.ExtraData"/> instance.
/// </remarks>
public sealed class BftBlockHeader(
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
    private volatile DecodedExtraData? _cache;

    /// <summary>The extra data codec of the consensus era this header belongs to.</summary>
    public IBftExtraDataCodec Codec { get; init; } = QbftExtraDataCodec.Instance;

    /// <summary>Decodes the consensus payload, throwing on malformed extra data.</summary>
    public BftExtraData GetBftExtraData()
    {
        byte[] current = ExtraData;
        DecodedExtraData? cache = _cache;
        if (cache is null || !ReferenceEquals(cache.Source, current))
        {
            cache = new DecodedExtraData(current, Codec.Decode(current));
            _cache = cache;
        }

        return cache.Value;
    }

    /// <summary>Both halves of the cache, published by a single write so a reader never pairs new bytes with an old decode.</summary>
    private sealed record DecodedExtraData(byte[] Source, BftExtraData Value);

    public bool TryGetBftExtraData([NotNullWhen(true)] out BftExtraData? extraData)
    {
        try
        {
            extraData = GetBftExtraData();
            return true;
        }
        catch (Exception e) when (e is RlpException or ArgumentException or IndexOutOfRangeException)
        {
            extraData = null;
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Malformed extra data falls back to the plain keccak so decoding never throws; validation rejects such headers anyway.</remarks>
    public ValueHash256 CalculateHash(RlpBehaviors behaviors = RlpBehaviors.None) =>
        !IsGenesis && TryGetBftExtraData(out BftExtraData? extraData)
            ? BftBlockHashing.CalculateOnChainHash(this, extraData, Codec)
            : BftBlockHashing.HashAsIs(this);

    /// <summary>Digest each validator signs to commit this block: extra data with the round kept and seals removed.</summary>
    public ValueHash256 CalculateCommitSealDigest() => BftBlockHashing.CalculateCommitSealDigest(this, GetBftExtraData(), Codec);

    public override BlockHeader CloneForProcessing()
    {
        BftBlockHeader clone = new(ParentHash, UnclesHash, Beneficiary, Difficulty, Number, GasLimit, Timestamp, ExtraData) { Codec = Codec };
        CopyProcessingFields(clone);
        return clone;
    }

    /// <summary>Copy of <paramref name="src"/> as a <see cref="BftBlockHeader"/>; returns the input when it already is one.</summary>
    public static BftBlockHeader UpgradeFrom(BlockHeader src, IBftExtraDataCodec codec)
    {
        if (src is BftBlockHeader qbft) return qbft;

        return new BftBlockHeader(
            src.ParentHash,
            src.UnclesHash,
            src.Beneficiary,
            in src.Difficulty,
            src.Number,
            src.GasLimit,
            src.Timestamp,
            src.ExtraData)
        {
            Codec = codec,
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
            BlobGasUsed = src.BlobGasUsed,
            ExcessBlobGas = src.ExcessBlobGas,
            SlotNumber = src.SlotNumber,
            IsPostMerge = src.IsPostMerge,
        };
    }
}
