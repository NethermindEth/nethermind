// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 4_096 + 1_024;

        // Halved when MaxStreamRetainedBytes doubled to 512 KiB, keeping the worst-case retained-bytes ceiling flat.
        // Assigned at startup (ApplyMemoryHint) from Init.InstructionStreamCacheSize, before any EVM execution
        // touches the cache: concurrent RPC load multiplies the simultaneously-hot code set, and a
        // set-associative cache conflict-evicts well before global capacity, pushing frames onto the
        // streamless interpreter path.
        public static int InstructionStreamCacheSize { get; set; } = 1_024;
    }
}
