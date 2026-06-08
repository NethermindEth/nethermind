// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Bridge that registers the AuRa <see cref="BlockHeader"/> subclass with
/// <see cref="AuRaBlockHeaderHandler"/> so <c>ChainSpecLoader</c> and <c>HeaderDecoder</c>
/// can build AuRa headers without depending on this plugin.
/// </summary>
/// <remarks>
/// Registration happens in <see cref="AuRaPlugin"/>'s static constructor — touching the
/// <see cref="AuRaPlugin"/> type (e.g. via the embedded plugins list in the runner) triggers
/// it well before any chainspec or wire-format decoding runs.
/// </remarks>
internal sealed class AuRaBlockHeaderHandlerImpl : IAuRaBlockHeaderHandler
{
    public static readonly AuRaBlockHeaderHandlerImpl Instance = new();

    public BlockHeader CreateBlockHeader(
        Hash256? parentHash,
        Hash256? unclesHash,
        Address? beneficiary,
        in UInt256 difficulty,
        long number,
        long gasLimit,
        ulong timestamp,
        byte[] extraData)
        => new AuRaBlockHeader(parentHash, unclesHash, beneficiary, in difficulty, number, gasLimit, timestamp, extraData);

    public BlockHeader UpgradeToAuRa(BlockHeader header)
    {
        if (header is AuRaBlockHeader) return header;

        // Clone fields onto an AuRaBlockHeader. Used when a code path constructs a base
        // BlockHeader but the final type must be AuRa (e.g. AuRa block producer wiring up
        // the step before the sealer has produced the signature).
        return new AuRaBlockHeader(
            header.ParentHash,
            header.UnclesHash,
            header.Beneficiary,
            in header.Difficulty,
            header.Number,
            header.GasLimit,
            header.Timestamp,
            header.ExtraData)
        {
            Author = header.Author,
            StateRoot = header.StateRoot,
            TxRoot = header.TxRoot,
            ReceiptsRoot = header.ReceiptsRoot,
            Bloom = header.Bloom,
            GasUsed = header.GasUsed,
            MixHash = header.MixHash,
            Nonce = header.Nonce,
            Hash = header.Hash,
            TotalDifficulty = header.TotalDifficulty,
            BaseFeePerGas = header.BaseFeePerGas,
            WithdrawalsRoot = header.WithdrawalsRoot,
            ParentBeaconBlockRoot = header.ParentBeaconBlockRoot,
            RequestsHash = header.RequestsHash,
            BlockAccessListHash = header.BlockAccessListHash,
            BlobGasUsed = header.BlobGasUsed,
            ExcessBlobGas = header.ExcessBlobGas,
            SlotNumber = header.SlotNumber,
            IsPostMerge = header.IsPostMerge,
        };
    }

    public BlockHeader SetSeal(BlockHeader header, long step, byte[] signature)
    {
        AuRaBlockHeader aura = (AuRaBlockHeader)UpgradeToAuRa(header);
        aura.AuRaStep = step;
        aura.AuRaSignature = signature;
        return aura;
    }

    public bool TryGetSeal(BlockHeader header, out long step, out byte[]? signature)
    {
        if (header is AuRaBlockHeader aura && aura.AuRaStep.HasValue && aura.AuRaSignature is not null)
        {
            step = aura.AuRaStep.Value;
            signature = aura.AuRaSignature;
            return true;
        }

        step = 0;
        signature = null;
        return false;
    }
}
