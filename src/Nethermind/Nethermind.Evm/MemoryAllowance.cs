// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm
{
    public static class MemoryAllowance
    {
        public static int CodeCacheSize { get; } = 4_096 + 1_024;

        /// <summary>Entries of the process-wide code cache for code of at most 8 KiB; at most ~150 MB.</summary>
        public static int SmallCodeCacheSize { get; } = 16_384;

        /// <summary>Entries of the process-wide code cache for code of at most 24 KiB; at most ~110 MB.</summary>
        public static int MediumCodeCacheSize { get; } = 4_096;

        /// <summary>Entries of the process-wide code cache for larger code; at most ~75 MB at 64 KiB.</summary>
        public static int LargeCodeCacheSize { get; } = 1_024;
    }
}
