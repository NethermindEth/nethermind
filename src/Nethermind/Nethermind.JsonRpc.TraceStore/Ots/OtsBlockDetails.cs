// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>A block summary with execution fees and recorded consensus issuance.</summary>
public sealed class OtsBlockDetails
{
    /// <summary>Block RPC fields without transactions, with a transaction count and null logs bloom.</summary>
    public required Dictionary<string, object?> Block { get; init; }
    /// <summary>The block-author and uncle issuance recorded for this block.</summary>
    public required OtsIssuance Issuance { get; init; }
    /// <summary>Total execution gas fees, including the base fee and excluding blob fees, in wei.</summary>
    public UInt256 TotalFees { get; init; }
}

/// <summary>Consensus rewards categorized as block-author and uncle issuance.</summary>
public sealed class OtsIssuance
{
    /// <summary>Block-author rewards, including uncle inclusion rewards, in wei.</summary>
    public UInt256 BlockReward { get; init; }
    /// <summary>Rewards paid to uncle authors, in wei.</summary>
    public UInt256 UncleReward { get; init; }
    /// <summary>The sum of block-author and uncle rewards, in wei.</summary>
    public UInt256 Issuance => BlockReward + UncleReward;
}

/// <summary>A summarized block with matching transaction and receipt pages.</summary>
public sealed class OtsBlockTransactions
{
    /// <summary>Block RPC fields containing the selected transaction page and the block's full transaction count.</summary>
    public required Dictionary<string, object?> Fullblock { get; init; }
    /// <summary>Receipts in page transaction order, with null logs and logs blooms.</summary>
    public required Dictionary<string, object?>[] Receipts { get; init; }
}
