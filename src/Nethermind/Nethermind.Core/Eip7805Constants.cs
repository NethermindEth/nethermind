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
}
