// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[TestFixture]
public class TrieRootFixtureTests
{
    private static TrieRootFixture CreateSmall(TrieRootFixture.TrieKind kind) =>
        TrieRootFixture.Create($"small-{kind}", kind, seed: 7, parentCount: 10_000, modifyCount: 100, insertCount: 20, deleteCount: 15);

    [TestCase(TrieRootFixture.TrieKind.State)]
    [TestCase(TrieRootFixture.TrieKind.Storage)]
    public void Generation_is_deterministic(TrieRootFixture.TrieKind kind)
    {
        TrieRootFixture first = CreateSmall(kind);
        TrieRootFixture second = CreateSmall(kind);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.ParentRoot, Is.EqualTo(first.ParentRoot), "parent root");
            Assert.That(second.ExpectedRoot, Is.EqualTo(first.ExpectedRoot), "expected root");
            Assert.That(second.Updates, Has.Length.EqualTo(first.Updates.Length), "update count");
            Assert.That(second.ExpectedNodes, Has.Count.EqualTo(first.ExpectedNodes.Count), "expected node count");
        }

        for (int i = 0; i < first.Updates.Length; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(second.Updates[i].Path, Is.EqualTo(first.Updates[i].Path), $"update key {i}");
                Assert.That(second.Updates[i].Value.AsSpan().SequenceEqual(first.Updates[i].Value), $"update value {i}");
            }
        }
    }

    [TestCase(TrieRootFixture.TrieKind.State)]
    [TestCase(TrieRootFixture.TrieKind.Storage)]
    public void Expected_root_matches_serial_patricia(TrieRootFixture.TrieKind kind)
    {
        TrieRootFixture fixture = CreateSmall(kind);

        PatriciaTree tree = new(new RawScopedTrieStore(fixture.ParentStorage), fixture.ParentRoot, true, NullLogManager.Instance);
        foreach (PatriciaTree.BulkSetEntry entry in fixture.Updates)
        {
            tree.Set(entry.Path.Bytes, entry.Value);
        }

        tree.UpdateRootHash(canBeParallel: false);

        Assert.That(tree.RootHash, Is.EqualTo(fixture.ExpectedRoot));
    }

    [TestCase(TrieRootFixture.TrieKind.State)]
    [TestCase(TrieRootFixture.TrieKind.Storage)]
    public void Expected_nodes_are_unique_hash_consistent_and_include_root(TrieRootFixture.TrieKind kind)
    {
        TrieRootFixture fixture = CreateSmall(kind);

        Assert.That(fixture.ExpectedNodes, Is.Not.Empty);

        HashSet<(TreePath, Hash256)> seen = [];
        bool rootSeen = false;
        foreach (TrieRootFixture.PersistedTrieNode node in fixture.ExpectedNodes)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(seen.Add((node.Path, node.Hash)), $"duplicate node at {node.Path} {node.Hash}");
                Assert.That(ValueKeccak.Compute(node.Rlp).ToCommitment(), Is.EqualTo(node.Hash), $"hash mismatch at {node.Path}");
            }

            rootSeen |= node.Path.Length == 0 && node.Hash == fixture.ExpectedRoot;
        }

        Assert.That(rootSeen, "expected nodes must include the new root");
    }

    // The complete fixture content (ordered updates and recorded nodes, both value encodings) is
    // frozen through one digest per kind; the 1M-parent gate fixtures are too heavy for CI and are
    // covered by the [Explicit] printer plus the shared derivation code exercised here.
    [TestCase(TrieRootFixture.TrieKind.State, "0x7a4a998f385d309cc7b7635f090fd2a933a9a04cf476e5cb27d2b4c62bc41aaf")]
    [TestCase(TrieRootFixture.TrieKind.Storage, "0xbc7d0770c0ad534cab8af7122b68558f99191749a092138149e46aee3bc517dd")]
    public void Small_fixture_content_digest_is_frozen(TrieRootFixture.TrieKind kind, string digest) =>
        Assert.That(ComputeContentDigest(CreateSmall(kind)).ToString(), Is.EqualTo(digest));

    private static Hash256 ComputeContentDigest(TrieRootFixture fixture)
    {
        using MemoryStream buffer = new();
        Span<byte> lengthScratch = stackalloc byte[4];
        buffer.Write(fixture.ParentRoot.Bytes);
        buffer.Write(fixture.ExpectedRoot.Bytes);

        foreach (PatriciaTree.BulkSetEntry update in fixture.Updates)
        {
            buffer.Write(update.Path.Bytes);
            BinaryPrimitives.WriteInt32LittleEndian(lengthScratch, update.Value.Length);
            buffer.Write(lengthScratch);
            buffer.Write(update.Value);
        }

        foreach (TrieRootFixture.PersistedTrieNode node in fixture.ExpectedNodes)
        {
            buffer.WriteByte((byte)node.Path.Length);
            buffer.Write(node.Path.Path.Bytes);
            buffer.Write(node.Hash.Bytes);
            BinaryPrimitives.WriteInt32LittleEndian(lengthScratch, node.Rlp.Length);
            buffer.Write(lengthScratch);
            buffer.Write(node.Rlp);
        }

        return ValueKeccak.Compute(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)).ToCommitment();
    }

    // Frozen generator outputs for the small gate fixtures; a change here means the fixture
    // definition changed and every recorded baseline is invalid.
    [TestCase("storage-tiny",
        "0x854fe66d37468fe87dbf0917d436440aa6a32c292cd25dc351bca3d628418d73",
        "0x1f41d894f60332148f536e5cee01219e810e352265b4d5d96f3122fd09505ae8")]
    [TestCase("storage-realblocks",
        "0xad0f7fd8011fc9bcaee48d327edad63b41880f497ff8688adc0fe4f90a0d1b79",
        "0x8eceb46a94edb75009800982eda101df4ed0454a1d8fa1c011b1f120668af605")]
    public void Gate_fixture_roots_are_frozen(string name, string parentRoot, string expectedRoot)
    {
        TrieRootFixture fixture = TrieRootFixture.CreateGateFixture(name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fixture.ParentRoot.ToString(), Is.EqualTo(parentRoot), "parent root");
            Assert.That(fixture.ExpectedRoot.ToString(), Is.EqualTo(expectedRoot), "expected root");
        }
    }

    [Test]
    [Explicit("Prints the frozen roots of every gate fixture, including the large ones; used to record baselines.")]
    public void Print_gate_fixture_roots()
    {
        foreach (string name in (string[])["storage-tiny", "storage-realblocks", "state-realblocks", "storage-dominant", "state-superblock"])
        {
            TrieRootFixture fixture = TrieRootFixture.CreateGateFixture(name);
            Console.WriteLine($"{name}: parent {fixture.ParentRoot} expected {fixture.ExpectedRoot} updates {fixture.Updates.Length} nodes {fixture.ExpectedNodes.Count}");
        }
    }
}
