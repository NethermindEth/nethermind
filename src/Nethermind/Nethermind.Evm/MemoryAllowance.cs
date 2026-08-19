// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 4_096 + 1_024;

        // Halved when MaxStreamRetainedBytes doubled to 512 KiB, keeping the worst-case retained-bytes ceiling flat.
        public static int InstructionStreamCacheSize { get; } = 4_096;
    }
}
