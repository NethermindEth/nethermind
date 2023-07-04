// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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

        public TrieException(string message) : base(message)
        {
        }

        public TrieException(string message, Exception inner) : base(message, inner)
        {
        }

        [DoesNotReturn]
        [StackTraceHidden]
        public static void ThrowOnLoadFailure(Span<byte> rawKey, ValueKeccak rootHash, Exception baseException)
        {
            if (baseException is MissingNodeException nodeException && nodeException.NodeHash == rootHash)
            {
                throw new TrieException($"Failed to load root hash {rootHash} while loading key {rawKey.ToHexString()}.", baseException);
            }
            throw new TrieException($"Failed to load key {rawKey.ToHexString()} from root hash {rootHash}.", baseException);
        }
    }
}
