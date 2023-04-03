// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
}
