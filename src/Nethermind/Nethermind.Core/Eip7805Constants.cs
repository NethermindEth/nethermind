// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Transport bounds on the inclusion lists an EIP-7805 committee can produce.</summary>
/// <remarks>
/// The engine API receives the committee's lists already flattened into one aggregate, so every aggregate
/// bound is a per-member bound times <see cref="InclusionListCommitteeSize"/>. They exist to cap decode
/// work on untrusted input; none of them is a consensus rule.
/// </remarks>
public static class Eip7805Constants
{
    /// <summary><c>IL_COMMITTEE_SIZE</c>: members whose lists one aggregate spans.</summary>
    public const int InclusionListCommitteeSize = 16;

    /// <summary><c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c>: byte cap on one member's list.</summary>
    public const int MaxBytesPerInclusionList = 8192;

    /// <summary>Entries one member's list can carry.</summary>
    /// <remarks>
    /// Consensus gossip caps a member's <c>transactions</c> at <see cref="MaxBytesPerInclusionList"/> and an
    /// empty entry still costs its SSZ offset, so the cap divided by the offset width is the entry ceiling
    /// the transport enforces. Entries that decode to nothing are conforming, so no tighter bound holds.
    /// </remarks>
    public const int MaxTransactionsPerInclusionList = MaxBytesPerInclusionList / SszOffsetBytes;

    /// <summary>Transaction bytes the flattened aggregate can carry.</summary>
    public const int MaxAggregateInclusionListBytes = InclusionListCommitteeSize * MaxBytesPerInclusionList;

    /// <summary>Entries the flattened aggregate can carry.</summary>
    public const int MaxAggregateInclusionListTransactions = InclusionListCommitteeSize * MaxTransactionsPerInclusionList;

    private const int SszOffsetBytes = 4;
}
