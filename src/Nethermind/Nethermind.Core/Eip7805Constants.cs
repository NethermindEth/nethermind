// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Transport bounds on the inclusion lists an EIP-7805 committee can produce.</summary>
/// <remarks>
/// The engine API receives one list per committee position, so every aggregate bound is a per-member bound
/// times <see cref="InclusionListCommitteeSize"/>. They exist to cap decode work on untrusted input; none of
/// them is a consensus rule.
/// </remarks>
public static class Eip7805Constants
{
    /// <summary><c>INCLUSION_LIST_COMMITTEE_SIZE</c>: committee positions, each with a list of its own.</summary>
    public const int InclusionListCommitteeSize = 16;

    /// <summary><c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c>: byte cap on one member's list.</summary>
    public const int MaxBytesPerInclusionList = 8192;

    /// <summary>Entries one member's list can carry.</summary>
    /// <remarks>
    /// Consensus gossip caps the summed byte length of a member's <c>transactions</c> at
    /// <see cref="MaxBytesPerInclusionList"/> and rejects an empty entry, so every entry costs at least one byte.
    /// </remarks>
    public const int MaxTransactionsPerInclusionList = MaxBytesPerInclusionList;

    /// <summary>Entries the committee's lists can carry together.</summary>
    public const int MaxAggregateInclusionListTransactions = InclusionListCommitteeSize * MaxTransactionsPerInclusionList;
}
