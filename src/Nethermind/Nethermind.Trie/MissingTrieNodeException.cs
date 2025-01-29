// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class MissingTrieNodeException : TrieException
{
    public Hash256 Address { get; }
    public TreePath Path { get; }
    public Hash256 Hash { get; }

    public MissingTrieNodeException(string message, Hash256? address, TreePath path, Hash256 hash, Exception? innerException = null) : base(message, innerException)
    {
        Address = address;
        Path = path;
        Hash = hash;
    }
}
