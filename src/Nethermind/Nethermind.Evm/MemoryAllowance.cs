// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 16_384;
        public static int InstructionStreamCacheSize { get; } = 2_048;
    }
}
