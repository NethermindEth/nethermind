// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Native;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

public class NativePatriciaBulkSetTests
{
    public static IEnumerable<TestCaseData> BulkSetTestGen()
    {
        foreach (TestCaseData testCase in PatriciaTreeBulkSetterTests.BulkSetTestGen()) yield return testCase;

        List<(Hash256 key, byte[] value)> existing = PatriciaTreeBulkSetterTests.GenRandomOfLength(10000, seed: 1);
        List<(Hash256 key, byte[] value)> changes =
        [
            .. existing.Take(2500).Select(item => (item.key, (byte[])[.. item.value, 1])),
            .. existing.Skip(2500).Take(2500).Select(item => (item.key, (byte[])null)),
            .. PatriciaTreeBulkSetterTests.GenRandomOfLength(5000, seed: 2),
        ];
        yield return new TestCaseData(existing, changes).SetName("parallel updates, deletes and inserts");
    }

    [TestCaseSource(nameof(BulkSetTestGen))]
    public void BulkSet_root_matches_patricia_tree(List<(Hash256 key, byte[] value)> existingItems, List<(Hash256 key, byte[] value)> items)
    {
        Assume.That(NativePatriciaBulkSet.IsAvailable, "libnethermind_patricia is not built");

        PatriciaTree tree = new(new RawScopedTrieStore(new MemDb()), LimboLogs.Instance);
        foreach ((Hash256 key, byte[] value) in existingItems.Concat(items)) tree.Set(key.Bytes, value);
        tree.UpdateRootHash();

        ConcurrentDictionary<TreePath, byte[]> nodes = new();
        DictionaryNodeStore store = new(nodes, nodes);
        Hash256 existingRoot = BulkSet(store, Keccak.EmptyTreeHash, existingItems);
        Assert.That(BulkSet(store, existingRoot, items), Is.EqualTo(tree.RootHash));
    }

    private static Hash256 BulkSet(INativeNodeStore store, Hash256 root, List<(Hash256 key, byte[] value)> items)
    {
        NativeBulkSetEntry[] entries = new NativeBulkSetEntry[items.Count];
        int offset = 0;
        for (int i = 0; i < items.Count; i++)
        {
            int length = items[i].value?.Length ?? 0;
            entries[i] = new NativeBulkSetEntry(items[i].key.ValueHash256, offset, length);
            offset += length;
        }

        return NativePatriciaBulkSet.BulkSet(store, root.ValueHash256, entries, items.SelectMany(item => item.value ?? []).ToArray());
    }
}
