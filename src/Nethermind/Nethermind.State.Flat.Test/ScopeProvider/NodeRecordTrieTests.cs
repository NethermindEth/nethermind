// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.ScopeProvider;

public class NodeRecordTrieTests
{
    [Test]
    public void TryGet_reads_through_an_embedded_extension_and_branch()
    {
        // One-byte values under keys sharing 62 nibbles embed their branch, and the key diverging at nibble 60 embeds the one-nibble
        // extension above it, which keccak keys only theoretically do.
        ValueHash256 keyA = new("0x1000000000000000000000000000000000000000000000000000000000000001");
        ValueHash256 keyB = new("0x1000000000000000000000000000000000000000000000000000000000000012");
        ValueHash256 divergingKey = new("0x1000000000000000000000000000000000000000000000000000000000001000");
        byte[] divergingValue = Bytes.FromHexString("0x7777777777777777777777777777777777777777777777777777777777777777");
        ResourcePool pool = new(new FlatDbConfig());
        using INodeRecordBuffer buffer = FlatTestHelpers.NewTestBuffer();
        using INodeRecordBufferPin retain = buffer.PinHead();
        using SnapshotBundle bundle = new(FlatTestHelpers.MakeBundle(pool), buffer, pool, ResourcePool.Usage.MainBlockProcessing);
        NodeRecordTrie trie = new(bundle, null, (ValueKeccak.EmptyTreeHash, 0));
        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(3))
        {
            entries.Add(new PatriciaTree.BulkSetEntry(keyA, [1]));
            entries.Add(new PatriciaTree.BulkSetEntry(keyB, [2]));
            entries.Add(new PatriciaTree.BulkSetEntry(divergingKey, divergingValue));
            trie.BulkSet(entries, canParallelize: false);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(trie.TryGet(keyA), Is.EqualTo(Bytes.FromHexString("0x01")));
            Assert.That(trie.TryGet(keyB), Is.EqualTo(Bytes.FromHexString("0x02")));
            Assert.That(trie.TryGet(divergingKey), Is.EqualTo(divergingValue));
            Assert.That(trie.TryGet(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000023")), Is.Null, "an empty child of the embedded branch");
            Assert.That(trie.TryGet(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000013")), Is.Null, "a key diverging in an embedded leaf");
            Assert.That(trie.TryGet(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000100")), Is.Null, "a key diverging in the embedded extension");
        }
    }
}
