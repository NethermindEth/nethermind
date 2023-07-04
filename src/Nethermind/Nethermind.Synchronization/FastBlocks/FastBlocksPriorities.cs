// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.FastBlocks
{
    internal static class FastBlocksPriorities
    {
        /// <summary>
        /// Batches that are so close to the lowest inserted header will be prioritized
        /// </summary>
        // public const long ForHeaders = 16 * 1024;
        public const long ForHeaders = 16 * 1024;
    }
}
