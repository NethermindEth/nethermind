// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Pruning
{
    [Serializable]
    public class TrieStoreException : TrieException
    {
        public TrieStoreException() { }

        public TrieStoreException(string message) : base(message) { }

        public TrieStoreException(string message, Exception inner) : base(message, inner) { }
    }
}
