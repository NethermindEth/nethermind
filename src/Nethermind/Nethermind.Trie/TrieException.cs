// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        public static TrieException CreateOnLoadFailure(Span<byte> rawKey, ValueKeccak rootHash, Exception baseException)
        {
            if (baseException is MissingNodeException nodeException && nodeException.NodeHash == rootHash)
            {
                return new MissingRootHashException(rootHash, baseException);
            }
            return new TrieException($"Failed to load key {rawKey.ToHexString()} from root hash {rootHash}.", baseException);
        }
    }
}
