// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Rejects a payload carrying more transactions than a child of its parent could hold.</summary>
/// <remarks>
/// Runs before the transactions are decoded, so padding a payload with entries cannot make the node decode them all.
/// The payload's own gas limit cannot bound the count: it is checked against the parent only after decoding.
/// Chains whose gas limit is not bound by <see cref="IReleaseSpec.GasLimitBoundDivisor"/> register <see cref="Disabled"/>.
/// </remarks>
public sealed class PayloadTransactionCountGuard(IBlockFinder? blockFinder)
{
    /// <summary>Gas used by the cheapest transaction on any fork: the base cost halved by the pre-EIP-3529 refund cap.</summary>
    /// <remarks>EIP-2780's lower base cost still sits above it.</remarks>
    private const ulong MinTransactionGas = GasCostOf.Transaction / 2;

    public static readonly PayloadTransactionCountGuard Disabled = new(null);

    /// <returns><c>false</c> when the count cannot fit; <c>true</c> when it can, or when the parent is unknown.</returns>
    public bool IsWithinParentGasLimit(ExecutionPayload payload, IReleaseSpec spec, [NotNullWhen(false)] out string? error)
    {
        error = null;
        if (blockFinder is null || spec.GasLimitBoundDivisor == 0) return true;

        BlockHeader? parent = blockFinder.FindHeader(payload.ParentHash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parent is null) return true;

        // Split so the parent's gas limit plus its allowed rise cannot overflow; the +1 absorbs the rounding.
        ulong maxTransactions = parent.GasLimit / MinTransactionGas + parent.GasLimit / spec.GasLimitBoundDivisor / MinTransactionGas + 1;
        if ((ulong)payload.Transactions.Length <= maxTransactions) return true;

        error = $"Transaction count {payload.Transactions.Length} exceeds {maxTransactions}, the most the parent gas limit allows";
        return false;
    }
}
