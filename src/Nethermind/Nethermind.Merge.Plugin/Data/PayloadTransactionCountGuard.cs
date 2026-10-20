// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Rejects a payload carrying more transactions than a child of its parent could hold.</summary>
/// <remarks>
/// Runs before the transactions are decoded, so padding a payload with entries cannot make the node decode them all.
/// The payload's own gas limit cannot bound the count: it is checked against the parent only after decoding.
/// Chains whose gas limit is not bound by <see cref="IReleaseSpec.GasLimitBoundDivisor"/> register <see cref="Disabled"/>.
/// </remarks>
public sealed class PayloadTransactionCountGuard(IBlockTree? blockTree, IPoSSwitcher poSSwitcher)
{
    /// <summary>Gas used by the cheapest transaction on any fork: the base cost halved by the pre-EIP-3529 refund cap.</summary>
    /// <remarks>EIP-2780's lower base cost still sits above it.</remarks>
    internal const ulong MinTransactionGas = GasCostOf.Transaction / 2;

    /// <summary>A guard that permits any count for chains without a parent-relative gas limit bound.</summary>
    public static readonly PayloadTransactionCountGuard Disabled = new(null, NoPoS.Instance);

    /// <summary>Checks whether the payload's transaction count can fit within its parent's gas limit bound.</summary>
    /// <param name="latestValidHash">On rejection, zero for a PoW parent, the parent's hash for a processed PoS parent; otherwise <c>null</c>.</param>
    /// <returns><c>false</c> when the count cannot fit; <c>true</c> when it can, or when the parent is unknown.</returns>
    public bool IsWithinParentGasLimit(ExecutionPayload payload, IReleaseSpec spec, [NotNullWhen(false)] out string? error, out Hash256? latestValidHash)
    {
        error = null;
        latestValidHash = null;
        if (blockTree is null || spec.GasLimitBoundDivisor == 0) return true;

        BlockHeader? parent = blockTree.FindHeader(payload.ParentHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded | BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parent is null) return true;

        // The fork-block elasticity jump mirrors Eip1559GasLimitAdjuster; UInt128 because hive sets parent gas limits up to ulong.MaxValue.
        UInt128 gasLimit = parent.GasLimit;
        if (spec.Eip1559TransitionBlock == payload.BlockNumber) gasLimit *= spec.ElasticityMultiplier;
        UInt128 maxTransactions = (gasLimit + gasLimit / spec.GasLimitBoundDivisor) / MinTransactionGas;
        if ((UInt128)payload.Transactions.Length <= maxTransactions) return true;

        error = $"Transaction count {payload.Transactions.Length} exceeds {maxTransactions}, the most the parent gas limit allows";
        // Paris reports a PoW latest valid ancestor as zero.
        if (!poSSwitcher.IsPostMerge(parent)) latestValidHash = Keccak.Zero;
        else if (blockTree.WasProcessed(parent.Number, payload.ParentHash)) latestValidHash = payload.ParentHash;
        return false;
    }
}
