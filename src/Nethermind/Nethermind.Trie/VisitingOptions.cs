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

        public bool FullDbScan { get; set; }
    }
}
