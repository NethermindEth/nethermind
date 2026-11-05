// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.JsonRpc.TraceStore.Ots;

/// <summary>A block summary with execution fees and recorded consensus issuance.</summary>
public sealed class OtsBlockDetails
{
    public required Dictionary<string, object?> Block { get; init; }
    public required OtsIssuance Issuance { get; init; }
    public UInt256 TotalFees { get; init; }
}

/// <summary>Consensus rewards categorized as block-author and uncle issuance.</summary>
public sealed class OtsIssuance
{
    public UInt256 BlockReward { get; init; }
    public UInt256 UncleReward { get; init; }
    public UInt256 Issuance => BlockReward + UncleReward;
}

/// <summary>A summarized block with matching transaction and receipt pages.</summary>
public sealed class OtsBlockTransactions
{
    public required Dictionary<string, object?> Fullblock { get; init; }
    public required Dictionary<string, object?>[] Receipts { get; init; }
}
