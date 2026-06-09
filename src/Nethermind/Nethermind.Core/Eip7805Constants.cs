// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7805Constants
{
    public const int MaxBytesPerInclusionList = 8192;
    // 32 bytes as conservative lower bound for transaction size
    public const int MinTransactionSizeBytesLower = 32;
    public const int MinTransactionSizeBytesUpper = 100;
    // Theoretical upper bound (8192 / 32-byte lower-bound tx). Spec caps bytes, not tx count;
    // real-world tx sizes (~100+ B) put the practical max much lower. Used as a stackalloc bound.
    public const int MaxTransactionsPerInclusionList = MaxBytesPerInclusionList / MinTransactionSizeBytesLower;
}
