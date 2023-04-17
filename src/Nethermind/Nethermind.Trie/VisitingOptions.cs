// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie
{
    /// <summary>
    /// Options to run <see cref="ITreeVisitor"/> on trie.
    /// </summary>
    public class VisitingOptions
    {
        public static readonly VisitingOptions Default = new();

        /// <summary>
        /// Should visit accounts.
        /// </summary>
        public bool ExpectAccounts { get; init; } = true;

        /// <summary>
        /// Maximum number of threads that will be used to visit the trie.
        /// </summary>
        public int MaxDegreeOfParallelism { get; init; } = 1;

        /// <summary>
        /// Specify memory budget to run a batched trie visitor. Significantly reduce read iops as memory budget
        /// increase. Not effective below a certain amount, and above a certain amount it has no measurable difference
        /// or the size of the db can't fill a queue of that size. For goerli, its 256MB to 6GB. For mainnet, its 1GB to
        /// 12 GB. Effect may be larger on system with lower RAM due to bigger portion of uncached files, or system
        /// with slower SSD. Set to 0 to disable batched trie visitor.
        /// </summary>
        public long FullScanMemoryBudget { get; set; }
    }
}
