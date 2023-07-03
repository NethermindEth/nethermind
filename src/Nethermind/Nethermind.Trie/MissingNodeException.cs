// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

public class MissingNodeException : TrieException
{
    public ValueKeccak NodeHash { get; private set; }

    public MissingNodeException(Keccak keccak) : base($"Node {keccak} is missing from the DB")
    {
        NodeHash = keccak;
    }
}
