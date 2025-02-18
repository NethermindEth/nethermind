// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7805Constants
{
    public const int MaxBytesPerInclusionList = 8192;
    // 32 bytes as conservative lower bound for transaction size
    public const int MinTransactionSizeBytesLower = 32;
    public const int MinTransactionSizeBytesUpper = 100;
    public const int MaxTransactionsPerInclusionList = MaxBytesPerInclusionList / MinTransactionSizeBytesLower;
}
