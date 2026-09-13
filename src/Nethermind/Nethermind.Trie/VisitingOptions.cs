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
        private readonly int _maxDegreeOfParallelism = 1;

        /// <summary>
        /// Maximum number of threads that will be used to visit the trie.
        /// </summary>
        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            init
            {
                _maxDegreeOfParallelism = AdjustMaxDegreeOfParallelism(value);
            }
        }

        public static int AdjustMaxDegreeOfParallelism(int rawMaxDegreeOfParallelism) =>
            rawMaxDegreeOfParallelism switch
            {
                0 => Math.Max(Environment.ProcessorCount / 4, 1),
                <= -1 => Environment.ProcessorCount,
                _ => rawMaxDegreeOfParallelism
            };
    }
}
