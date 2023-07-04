// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool
{
    public static class MemoryAllowance
    {
        public static int MemPoolSize { get; set; } = 1 << 11;
        public static int TxHashCacheSize { get; set; } = 1 << 19;
    }
}
