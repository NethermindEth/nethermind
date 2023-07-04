// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.AccountAbstraction.Broadcaster
{
    public static class MemoryAllowance
    {
        public static int MemPoolSize { get; set; } = 1 << 11;
    }
}
