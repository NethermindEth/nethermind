// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 4_096 + 1_024;

        // Assigned at startup (ApplyMemoryHint) from Init.InstructionStreamCacheSize (default 4096),
        // before any EVM execution touches the cache: concurrent RPC load multiplies the
        // simultaneously-hot code set, and a set-associative cache conflict-evicts well before global
        // capacity, pushing frames onto the streamless interpreter path. The static default stays at
        // the old frugal 1024 for hosts that never run node init (tests, standalone tools).
        public static int InstructionStreamCacheSize { get; set; } = 1_024;
    }
}
