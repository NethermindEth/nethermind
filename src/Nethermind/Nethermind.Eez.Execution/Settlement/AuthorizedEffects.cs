// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement;

/// <summary>An inbound delivery an entry claims, checked against what it did on L2.</summary>
public readonly record struct AuthorizedInbound(int TransactionIndex, InboundObservation Observation);

/// <summary>
/// An outbound call an entry executes and the system load that stages it. The call's result is published only in DA,
/// so the entry's DA sidecar is <paramref name="BaseDaEntry"/> with that result, and the rolling hash the entry claims
/// must close <paramref name="PendingRollingHash"/> with it.
/// </summary>
/// <param name="PendingRollingHash">The entry's rolling hash up to the call's begin.</param>
/// <param name="ClaimedRollingHash">The rolling hash the L1 entry claims, which the call's end must reach.</param>
public readonly record struct AuthorizedOutbound(
    int LoadTransactionIndex,
    int TransactionIndex,
    ExecutionEntry BaseDaEntry,
    ValueHash256 PendingRollingHash,
    ValueHash256 ClaimedRollingHash);
