// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;

namespace Nethermind.Trie
{
    public class TrieException : Exception, IInternalNethermindException
    {
        public TrieException()
        {
        }

        public TrieException(string message) : base(message)
        {
        }

        public TrieException(string message, Exception inner) : base(message, inner)
        {
        }
    }

    public class MissingTrieNodeException : TrieException
    {
        public MissingTrieNodeException(string message, Exception inner, byte[] updatePath, int currentIndex) : base(message, inner)
        {
            UpdatePath = updatePath;
            CurrentIndex = currentIndex;
        }

        public byte[] UpdatePath { get; }
        public int CurrentIndex { get; }
    }
}
