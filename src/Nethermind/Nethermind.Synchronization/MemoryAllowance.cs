// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Synchronization
{
    public static class MemoryAllowance
    {
        public static ulong FastBlocksMemory = (ulong)128.MB();
    }
}
