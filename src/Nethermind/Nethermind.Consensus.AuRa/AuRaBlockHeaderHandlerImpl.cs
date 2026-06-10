// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Test-only bridge: registers a delegate with <see cref="AuRaBlockHeaderHandler"/> so core test
/// builders can stamp an AuRa seal onto a base <see cref="BlockHeader"/> without taking a
/// dependency on this plugin. Production code constructs <see cref="AuRaBlockHeader"/> directly.
/// </summary>
/// <remarks>
/// Registration runs in a <see cref="ModuleInitializerAttribute">module initializer</see>, so any
/// assembly that references <c>Nethermind.Consensus.AuRa</c> wires the handler at load time.
/// </remarks>
internal sealed class AuRaBlockHeaderHandlerImpl : IAuRaBlockHeaderHandler
{
    public static readonly AuRaBlockHeaderHandlerImpl Instance = new();

#pragma warning disable CA2255 // ModuleInitializer is the documented mechanism for guaranteed
    // load-time registration of an inter-assembly bridge.
    [ModuleInitializer]
    internal static void Register() => AuRaBlockHeaderHandler.Register(Instance);
#pragma warning restore CA2255

    public BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature)
    {
        AuRaBlockHeader aura = UpgradeToAuRa(header);
        aura.AuRaStep = step;
        aura.AuRaSignature = signature;
        return aura;
    }

    private static AuRaBlockHeader UpgradeToAuRa(BlockHeader header)
    {
        if (header is AuRaBlockHeader aura) return aura;

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
}
