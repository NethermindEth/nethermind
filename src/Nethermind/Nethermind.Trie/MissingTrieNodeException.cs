// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class MissingTrieNodeException(string message, Hash256? address, TreePath path, Hash256 hash, Exception? innerException = null) : TrieException(message, innerException)
{
    public Hash256 Address { get; } = address;
    public TreePath Path { get; } = path;
    public Hash256 Hash { get; } = hash;
}
