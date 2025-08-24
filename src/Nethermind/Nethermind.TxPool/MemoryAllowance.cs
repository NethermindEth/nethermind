// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool
{
    public static class MemoryAllowance
    {
        public static int MemPoolSize { get; set; } = 2_048;
        public static int TxHashCacheSize { get; set; } = 131_072;
        public static int RequestedTxHashCacheSize { get; set; } = MemPoolSize * 2;
    }
}
