// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Migration;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Migration;

[TestFixture]
public class HalfPathFallbackPersistenceTests
{
    private const int AccountCount = 32;
    private const int SlotsPerAccount = 8;

    public enum Copied { Nothing, FirstAccountWithoutSlots, MidStorage, Everything }

    [Test]
    public void Reads_match_the_trie_however_much_is_copied([Values] Copied copied)
    {
        using MemDb halfPathDb = new();
        NodeStorage halfPath = new(halfPathDb);
        (Hash256 root, TestAccount[] accounts) = BuildState(halfPath);
        MigrationCursor cursor = copied switch
        {
            Copied.Nothing => MigrationCursor.Start,
            Copied.FirstAccountWithoutSlots => new MigrationCursor(accounts[0].Path, null),
            Copied.MidStorage => new MigrationCursor(accounts[AccountCount / 2].Path, accounts[AccountCount / 2].Slots[SlotsPerAccount / 2].Path),
            _ => new MigrationCursor(accounts[^1].Path, accounts[^1].Slots[^1].Path),
        };

        // Flat holds exactly what a migration at the cursor has copied.
        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        IPersistence flat = new RocksDbPersistence(columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
        using (IPersistence.IWriteBatch batch = flat.CreateWriteBatch(StateId.PreGenesis, new StateId(1, root)))
        {
            foreach ((Hash256? address, TreePath path, TrieNode node) in CollectNodes(halfPath, root))
            {
                if (cursor.IsCopied(address, path)) Importer.WriteNode(batch, address, path, node);
            }
        }

        List<(Hash256? Address, TreePath Path)> halfPathReads = [];
        INodeStorage halfPathSpy = Substitute.For<INodeStorage>();
        halfPathSpy.Get(Arg.Any<Hash256?>(), Arg.Any<TreePath>(), Arg.Any<ValueHash256>(), Arg.Any<ReadFlags>()).Returns(call =>
        {
            halfPathReads.Add((call.ArgAt<Hash256?>(0), call.ArgAt<TreePath>(1)));
            return halfPath.Get(call.ArgAt<Hash256?>(0), call.ArgAt<TreePath>(1), call.ArgAt<ValueHash256>(2), call.ArgAt<ReadFlags>(3));
        });
        HalfPathMigrationCursor migrationCursor = new(columnsDb);
        migrationCursor.Advance(cursor);

        using IPersistence.IPersistenceReader reader = new HalfPathFallbackPersistence(flat, halfPathSpy, migrationCursor).CreateReader();

        using (Assert.EnterMultipleScope())
        {
            foreach (TestAccount account in accounts)
            {
                Assert.That(reader.GetAccount(account.Address), Is.EqualTo(account.Account), account.Address.ToString());
                foreach (TestSlot slot in account.Slots)
                {
                    UInt256 value = 0;
                    Assert.That(reader.TryGetSlot(account.Address, slot.Index, ref value), Is.True, $"{account.Address} {slot.Index}");
                    Assert.That(value, Is.EqualTo(slot.Value), $"{account.Address} {slot.Index}");
                }
            }

            UInt256 missingValue = 0;
            Assert.That(reader.GetAccount(TestItem.GetRandomAddress()), Is.Null, "missing account");
            Assert.That(reader.TryGetSlot(accounts[^1].Address, SlotsPerAccount + 1, ref missingValue), Is.False, "missing slot");
            Assert.That(halfPathReads, Has.None.Matches<(Hash256? Address, TreePath Path)>(read => cursor.IsCopied(read.Address, read.Path)),
                "what the cursor has passed comes from flat alone");
            Assert.That(new HalfPathMigrationCursor(columnsDb).Current, Is.EqualTo(cursor), "the cursor survives a restart");
        }
    }

    [Test]
    public async Task An_account_or_storage_deleted_after_the_migration_started_reads_as_missing()
    {
        // HalfPath keeps the state the migration started from; flat has moved on to one without the first account and
        // with the second account's storage destroyed.
        using MemDb halfPathDb = new();
        NodeStorage halfPath = new(halfPathDb);
        (_, TestAccount[] accounts) = BuildState(halfPath);
        TestAccount deleted = accounts[0];
        TestAccount destroyed = accounts[1];

        using MemDb laterDb = new();
        StateTree later = new(new RawScopedTrieStore(new NodeStorage(laterDb)), LimboLogs.Instance);
        later.Set(destroyed.Address, destroyed.Account.WithChangedStorageRoot(Keccak.EmptyTreeHash));
        later.Commit();

        using SnapshotableMemColumnsDb<FlatDbColumns> columnsDb = new();
        IPersistence flat = new RocksDbPersistence(columnsDb, LimboLogs.Instance, NullTrieNodeLog.Instance);
        await new Importer(new NodeStorage(laterDb), flat, LimboLogs.Instance).Copy(new StateId(2, later.RootHash));
        HalfPathMigrationCursor migrationCursor = new(columnsDb);
        migrationCursor.Advance(MigrationCursor.Start);

        HalfPathFallbackPersistence persistence = new(flat, halfPath, migrationCursor);
        UInt256 value = 0;
        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(reader.GetAccount(deleted.Address), Is.Null);
                Assert.That(reader.TryGetSlot(destroyed.Address, destroyed.Slots[0].Index, ref value), Is.False);
            }
        }

        persistence.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(migrationCursor.Current, Is.Null, "a wipe ends the migration");
            Assert.That(BasePersistence.ReadMigrationCursor(columnsDb.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        }
    }

    internal readonly record struct TestSlot(UInt256 Index, UInt256 Value, ValueHash256 Path);

    internal readonly record struct TestAccount(Address Address, Account Account, ValueHash256 Path, TestSlot[] Slots);

    /// <summary>Builds a state of accounts with storage in <paramref name="nodeStorage"/>, the accounts and their slots in path order.</summary>
    internal static (Hash256 Root, TestAccount[] Accounts) BuildState(INodeStorage nodeStorage)
    {
        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), LimboLogs.Instance);
        TestAccount[] accounts = new TestAccount[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            Address address = TestItem.GetRandomAddress();
            ValueHash256 accountPath = address.ToAccountPath;
            StorageTree storageTree = new(new RawScopedTrieStore(nodeStorage, accountPath.ToCommitment()), LimboLogs.Instance);
            TestSlot[] slots = new TestSlot[SlotsPerAccount];
            for (int s = 0; s < SlotsPerAccount; s++)
            {
                UInt256 index = (UInt256)s;
                ValueHash256 slotPath = default;
                StorageTree.ComputeKeyWithLookup(index, ref slotPath);
                slots[s] = new TestSlot(index, (UInt256)(i * SlotsPerAccount + s + 1), slotPath);
                storageTree.Set(index, slots[s].Value.ToBigEndian().WithoutLeadingZeros());
            }
            storageTree.Commit();

            Account account = TestItem.GenerateIndexedAccount(i + 1).WithChangedStorageRoot(storageTree.RootHash);
            stateTree.Set(address, account);
            accounts[i] = new TestAccount(address, account, accountPath, slots.OrderBy(static slot => slot.Path).ToArray());
        }
        stateTree.Commit();

        return (stateTree.RootHash, accounts.OrderBy(static account => account.Path).ToArray());
    }

    /// <summary>Every node of the trie at <paramref name="root"/>, storage tries included, in path order.</summary>
    internal static List<(Hash256? Address, TreePath Path, TrieNode Node)> CollectNodes(INodeStorage nodeStorage, Hash256 root)
    {
        NodeCollector collector = new();
        new PatriciaTree(new RawTrieStore(nodeStorage), LimboLogs.Instance).Accept(collector, root, new VisitingOptions { MaxDegreeOfParallelism = 1 });
        return collector.Nodes;
    }

    private sealed class NodeCollector : ITreeVisitor<TreePathContextWithStorage>
    {
        public List<(Hash256? Address, TreePath Path, TrieNode Node)> Nodes { get; } = [];
        public bool IsFullDbScan => true;
        public bool ShouldVisit(in TreePathContextWithStorage nodeContext, in ValueHash256 nextNode) => true;
        public void VisitTree(in TreePathContextWithStorage nodeContext, in ValueHash256 rootHash) { }
        public void VisitMissingNode(in TreePathContextWithStorage nodeContext, in ValueHash256 nodeHash) =>
            throw new InvalidOperationException($"Missing node {nodeHash} at {nodeContext.Path}");
        public void VisitBranch(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
        public void VisitExtension(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
        public void VisitLeaf(in TreePathContextWithStorage nodeContext, TrieNode node) => Nodes.Add((nodeContext.Storage, nodeContext.Path, node));
        public void VisitAccount(in TreePathContextWithStorage nodeContext, TrieNode node, in AccountStruct account) { }
    }
}
