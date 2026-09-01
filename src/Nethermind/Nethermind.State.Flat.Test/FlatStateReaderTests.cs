// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class FlatStateReaderTests
{
    [TestCase(-1)]
    [TestCase(int.MinValue)]
    public void Rejects_negative_trie_node_rlp_cache_capacity(int capacity)
    {
        FlatDbConfig config = new() { TrieNodeRlpCacheCapacity = capacity };

        Assert.That(
            () => new FlatStateReader(null!, null!, config, LimboLogs.Instance),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }
}
