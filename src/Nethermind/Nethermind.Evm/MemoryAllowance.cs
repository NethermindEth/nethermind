// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 1 << 13;
    }
}
