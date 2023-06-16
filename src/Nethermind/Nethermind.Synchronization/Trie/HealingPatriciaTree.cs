// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Synchronization.Trie;

public class HealingPatriciaTree : PatriciaTree
{
    public override byte[]? Get(Span<byte> rawKey, Keccak? rootHash = null)
    {
        try
        {
            return base.Get(rawKey, rootHash);
        }
        catch (MissingTrieNodeException)
        {
            // TODO: try recover using snap sync
            throw;
        }
    }
}
