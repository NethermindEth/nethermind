// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>A settlement claim that the batch, the calldata or the observed execution does not support.</summary>
public sealed class EezSettlementException(EezSettlementFailure failure, string message) : Exception(message)
{
    public EezSettlementException(string message) : this(EezSettlementFailure.Rejected, message)
    {
    }

    public EezSettlementFailure Failure { get; } = failure;

    /// <summary>The settling-block transaction whose effect cannot settle, so a composer can evict it and retry.</summary>
    public int? PoisonedTransactionIndex { get; init; }

    /// <summary>The batch entry whose inbound delivery reverted, so a composer can evict it and retry.</summary>
    public int? PoisonedEntryIndex { get; init; }
}
