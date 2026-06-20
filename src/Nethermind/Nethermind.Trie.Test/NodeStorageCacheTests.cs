// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NodeStorageCacheTests
{
    private static readonly SeqlockCache<NodeKey, byte[]>.ValueFactory<TestLoadState> s_load = Load;

    [Test]
    public void State_factory_is_not_cached_when_disabled()
    {
        NodeStorageCache cache = new();
        NodeKey key = new(null, TreePath.Empty, TestItem.KeccakA);
        TestLoadState state = new();

        byte[]? first = cache.GetOrAdd(in key, state, s_load);
        byte[]? second = cache.GetOrAdd(in key, state, s_load);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Calls, Is.EqualTo(2));
            Assert.That(first, Is.Not.SameAs(second));
        }
    }

    [Test]
    public void State_factory_is_cached_when_enabled()
    {
        NodeStorageCache cache = new() { Enabled = true };
        NodeKey key = new(null, TreePath.Empty, TestItem.KeccakA);
        TestLoadState state = new();

        byte[]? first = cache.GetOrAdd(in key, state, s_load);
        byte[]? second = cache.GetOrAdd(in key, state, s_load);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Calls, Is.EqualTo(1));
            Assert.That(first, Is.SameAs(second));
        }
    }

    private static byte[] Load(in NodeKey key, TestLoadState state)
    {
        state.Calls++;
        return [(byte)state.Calls];
    }

    private sealed class TestLoadState
    {
        public int Calls { get; set; }
    }
}
