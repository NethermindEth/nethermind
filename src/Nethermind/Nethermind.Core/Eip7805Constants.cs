// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7805Constants
{
    public const int MaxBytesPerInclusionList = 8192;
    // Conservative lower bound for an encoded transaction's size.
    public const int MinTransactionSizeBytes = 32;
    // Theoretical bound: the spec caps bytes, not tx count. Used as a stackalloc bound.
    public const int MaxTransactionsPerInclusionList = MaxBytesPerInclusionList / MinTransactionSizeBytes;
    // EIP-7805 IL_COMMITTEE_SIZE: the flattened newPayloadV6 aggregate spans at most this many members' lists.
    public const int InclusionListCommitteeSize = 16;
    // Upper bound on the flattened aggregate handed to newPayloadV6 (committee members × per-member byte cap).
    public const int MaxAggregateInclusionListBytes = InclusionListCommitteeSize * MaxBytesPerInclusionList;
}
