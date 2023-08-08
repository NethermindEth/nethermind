// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;

namespace Nethermind.Trie
{
    public class TrieException : Exception, IInternalNethermindException
    {
        public TrieException()
        {
        }

        public TrieException(string message, Exception? inner = null) : base(message, inner)
        {
        }
    }
}
