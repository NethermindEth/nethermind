// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    /// <summary>
    /// Announces a new root at the state tree.
    /// </summary>
    public class StateRootCommittedEventArgs : EventArgs
    {
        /// <summary>
        /// The Keccak of the new root of the state trie.
        /// </summary>
        public Keccak RootKeccak { get; }

        public StateRootCommittedEventArgs(Keccak rootKeccak)
        {
            RootKeccak = rootKeccak;
        }
    }
}
