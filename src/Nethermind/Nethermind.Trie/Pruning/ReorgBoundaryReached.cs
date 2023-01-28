// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Pruning
{
    /// <summary>
    /// Tells which number is safe to mark as a checkpoint if it was persisted before.
    /// </summary>
    public class ReorgBoundaryReached : EventArgs
    {
        public ReorgBoundaryReached(long blockNumber)
        {
            BlockNumber = blockNumber;
        }

        public long BlockNumber { get; }
    }
}
