// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// ChainSpec post-processor for AuRa: upgrades <c>Genesis.Header</c> to <see cref="AuRaBlockHeader"/>
/// and stamps the step + signature parsed from <c>genesis.seal.authorityRound</c> by the core
/// <see cref="ChainSpecLoader"/>. Registered via <c>.Intercept&lt;ChainSpec&gt;(...)</c> on the
/// AuRa module — runs after the core loader produced a base-typed genesis.
/// </summary>
public static class AuRaChainSpecLoader
{
    public static void ProcessChainSpec(ChainSpec chainSpec)
    {
        if (chainSpec.Genesis is null || chainSpec.GenesisAuRaSeal is null) return;

        BlockHeader src = chainSpec.Genesis.Header;
        AuRaBlockHeader upgraded = new(
            src.ParentHash,
            src.UnclesHash,
            src.Beneficiary,
            src.Difficulty,
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
            BlobGasUsed = src.BlobGasUsed,
            ExcessBlobGas = src.ExcessBlobGas,
            SlotNumber = src.SlotNumber,
            IsPostMerge = src.IsPostMerge,
            AuRaStep = chainSpec.GenesisAuRaSeal.Step,
            AuRaSignature = chainSpec.GenesisAuRaSeal.Signature,
        };

        chainSpec.Genesis = chainSpec.Genesis.WithReplacedHeader(upgraded);
    }
}
