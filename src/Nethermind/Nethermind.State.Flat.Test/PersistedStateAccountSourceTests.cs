// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class PersistedStateAccountSourceTests
{
    private const int AccountCount = 600;

    private Dictionary<TreePath, byte[]> _nodes = null!;
    private Dictionary<ValueHash256, Account> _accounts = null!;
    private Hash256 _root = null!;
    private int _loads;

    [SetUp]
    public void SetUp() => BuildTrie(AccountCount);

    private void BuildTrie(int accountCount)
    {
        _loads = 0;
        using MemDb trieDb = new();
        StateTree tree = new(new RawScopedTrieStore(trieDb), LimboLogs.Instance);
        _accounts = [];
        Random random = new(42);
        byte[] addressBytes = new byte[Address.Size];
        for (int i = 0; i < accountCount; i++)
        {
            random.NextBytes(addressBytes);
            Address address = new(addressBytes.ToArray());
            Account account = new((ulong)i, (UInt256)(i + 1));
            tree.Set(address, account);
            _accounts[address.ToAccountPath] = account;
        }

        tree.Commit();
        _root = tree.RootHash;

        // The persisted columns key trie nodes by path, without the hash.
        NodeCollector collector = new();
        tree.Accept(collector, _root);
        _nodes = collector.Nodes;
    }

    // Few accounts put their leaves right below the root, above the partition depth: every partition sharing the
    // leaf's first nibble reaches it, and only the one its hash starts with may report it.
    [Test]
    public void Every_account_is_read_once_in_the_partition_of_its_first_hash_byte([Values(AccountCount, 3)] int accountCount)
    {
        BuildTrie(accountCount);
        PersistedStateAccountSource source = CreateSource();
        Dictionary<ValueHash256, Account> read = [];

        for (int partition = 0; partition < 256; partition++)
        {
            source.ForEachAccount(_root, (byte)partition, (path, account) =>
            {
#pragma warning disable NUnit2045 // The assertions advance or mutate the same state before checking it; preserve fail-fast ordering.
                Assert.That(path.Bytes[0], Is.EqualTo(partition));
#pragma warning restore NUnit2045
                Assert.That(read.TryAdd(path, account), Is.True, $"account {path} was read twice");
            }, CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.Keys, Is.EquivalentTo(_accounts.Keys));
            foreach ((ValueHash256 path, Account account) in _accounts)
            {
                Assert.That(read[path].Balance, Is.EqualTo(account.Balance));
                Assert.That(read[path].Nonce, Is.EqualTo(account.Nonce));
            }
        }
    }

    [Test]
    public void A_partition_reads_only_its_own_subtree()
    {
        PersistedStateAccountSource source = CreateSource();

        const byte partition = 0x7f;
        int onItsPath = _nodes.Keys.Count(static path => path.Length switch
        {
            0 => true,
            1 => path.Path.Bytes[0] >> 4 == partition >> 4,
            _ => path.Path.Bytes[0] == partition,
        });

        source.ForEachAccount(_root, partition, static (_, _) => { }, CancellationToken.None);

        Assert.That(_loads, Is.EqualTo(onItsPath), "a partition loads the nodes on its path and in its subtree, nothing else");
    }

    [Test]
    public void A_missing_node_fails_the_partition_instead_of_skipping_its_accounts()
    {
        TreePath missing = _nodes.Keys.First(static path => path.Length == 2);
        _nodes.Remove(missing);
        byte partition = missing.Path.Bytes[0];

        Assert.That(() => CreateSource().ForEachAccount(_root, partition, static (_, _) => { }, CancellationToken.None),
            Throws.InstanceOf<TrieException>());
    }

    [Test]
    public void Cancellation_stops_the_walk()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.That(() => CreateSource().ForEachAccount(_root, 0, static (_, _) => { }, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    private PersistedStateAccountSource CreateSource()
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        reader.TryLoadStateRlp(Arg.Any<TreePath>(), Arg.Any<ReadFlags>()).Returns(call =>
        {
            _loads++;
            return _nodes.GetValueOrDefault(call.ArgAt<TreePath>(0));
        });
        IPersistence persistence = Substitute.For<IPersistence>();
        persistence.CreateReader().Returns(reader);
        return new PersistedStateAccountSource(persistence, LimboLogs.Instance);
    }

    private sealed class NodeCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        public Dictionary<TreePath, byte[]> Nodes { get; } = [];

        public bool IsFullDbScan => true;
        public bool ExpectAccounts => false;

        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) => throw new InvalidOperationException("incomplete test trie");
        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes[nodeContext.Path] = node.FullRlp.ToArray()!;
        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes[nodeContext.Path] = node.FullRlp.ToArray()!;
        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes[nodeContext.Path] = node.FullRlp.ToArray()!;
        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }
    }
}
