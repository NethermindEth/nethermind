// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.FlatPriorityRead.Test;

[Parallelizable(ParallelScope.All)]
public class PrioritySlotStoreTests
{
    private static readonly ValueHash256 KeyA = Keccak.Compute("a").ValueHash256;
    private static readonly ValueHash256 KeyB = Keccak.Compute("b").ValueHash256;
    private static readonly ValueHash256 KeyC = Keccak.Compute("c").ValueHash256;

    [Test]
    public void Readers_keep_the_commit_they_opened_on([Values] FlatPriorityReadBackend backend)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using IPrioritySlotStore store = TestPrioritySlotStores.Create(backend, directory.Path);

        Commit(store, State(1), writer =>
        {
            writer.Set(0, KeyA, 1);
            writer.Set(0, KeyB, UInt256.Zero);
            writer.Set(1, KeyA, 3);
        });
        IPrioritySlotStore.IReader first = store.BeginRead();

        Commit(store, State(2), writer =>
        {
            writer.Set(0, KeyA, 2);
            writer.Remove(0, KeyB);
            writer.Set(0, KeyC, 4);
        });
        using IPrioritySlotStore.IReader second = store.BeginRead();

        AssertSlots(first, State(1), (0, KeyA, 1), (0, KeyB, UInt256.Zero), (0, KeyC, null), (1, KeyA, 3));
        AssertSlots(second, State(2), (0, KeyA, 2), (0, KeyB, null), (0, KeyC, 4), (1, KeyA, 3));

        // With the oldest reader gone the next commit may fold earlier commits into place; readers must not notice.
        first.Dispose();
        Commit(store, State(3), _ => { });
        using IPrioritySlotStore.IReader third = store.BeginRead();

        AssertSlots(second, State(2), (0, KeyA, 2), (0, KeyB, null), (0, KeyC, 4), (1, KeyA, 3));
        AssertSlots(third, State(3), (0, KeyA, 2), (0, KeyB, null), (0, KeyC, 4), (1, KeyA, 3));
    }

    [Test]
    public void Trie_nodes_keep_the_commit_they_opened_on([Values(FlatPriorityReadBackend.Lmdb, FlatPriorityReadBackend.RocksDb)] FlatPriorityReadBackend backend)
    {
        TreePath root = TreePath.Empty;
        TreePath zero = TreePath.FromNibble([0x0]);
        TreePath branch = TreePath.FromNibble([0xa, 0x5]);

        using TempPath directory = TempPath.GetTempDirectory();
        using IPrioritySlotStore store = TestPrioritySlotStores.Create(backend, directory.Path);

        // The slot key shares its 32 bytes with the path of the branch node, and the empty root with the path of zero.
        Commit(store, State(1), writer =>
        {
            writer.SetNode(0, root, Bytes.FromHexString("c101"));
            writer.SetNode(0, branch, Bytes.FromHexString("c102"));
            writer.SetNode(1, root, Bytes.FromHexString("c103"));
            writer.Set(0, branch.Path, 5);
        });
        IPrioritySlotStore.IReader first = store.BeginRead();

        Commit(store, State(2), writer =>
        {
            writer.SetNode(0, root, Bytes.FromHexString("c104"));
            writer.RemoveNode(0, branch);
        });
        using IPrioritySlotStore.IReader second = store.BeginRead();

        AssertNodes(first, (0, root, "c101"), (0, zero, null), (0, branch, "c102"), (1, root, "c103"), (1, branch, null));
        AssertNodes(second, (0, root, "c104"), (0, zero, null), (0, branch, null), (1, root, "c103"));
        AssertSlots(first, State(1), (0, branch.Path, 5));
        AssertSlots(second, State(2), (0, branch.Path, 5));
        first.Dispose();
    }

    [Test]
    public void Concurrent_readers_never_see_part_of_a_commit([Values] FlatPriorityReadBackend backend)
    {
        const int commits = 100;
        const int keyCount = 64;
        ValueHash256[] keys = Enumerable.Range(0, keyCount).Select(i => Keccak.Compute(i.ToString()).ValueHash256).ToArray();

        using TempPath directory = TempPath.GetTempDirectory();
        using IPrioritySlotStore store = TestPrioritySlotStores.Create(backend, directory.Path);
        CommitRound(store, keys, 0);

        // Commit n sets every key to n, except that odd commits remove the odd keys.
        int checkedReads = 0;
        string? failure = null;
        using CancellationTokenSource stop = new();
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested && failure is null)
            {
                using IPrioritySlotStore.IReader reader = store.BeginRead();
                ulong round = reader.State.BlockNumber;
                for (int i = 0; i < keyCount; i++)
                {
                    bool expectRemoved = round % 2 == 1 && i % 2 == 1;
                    bool found = reader.TryGet(0, keys[i], out UInt256 value);
                    if (found == expectRemoved || (found && value != round))
                    {
                        failure = $"Round {round} key {i}: found {found}, value {value}";
                    }
                }

                Interlocked.Increment(ref checkedReads);
            }
        })).ToArray();

        for (int round = 1; round <= commits && failure is null; round++) CommitRound(store, keys, (ulong)round);
        stop.Cancel();
        Task.WaitAll(readers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.Null);
            Assert.That(checkedReads, Is.GreaterThan(0));
        }
    }

    [Test]
    public void RocksDb_store_uses_plain_table()
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using (IPrioritySlotStore store = TestPrioritySlotStores.Create(FlatPriorityReadBackend.RocksDb, directory.Path))
        {
            Commit(store, State(1), writer => writer.Set(0, KeyA, 1));
            using IPrioritySlotStore.IReader reader = store.BeginRead();
            AssertSlots(reader, State(1), (0, KeyA, 1), (0, KeyB, null));
        }

        string optionsFile = Directory.GetFiles(Path.Combine(directory.Path, "rocksdb"), "OPTIONS-*").Max()!;
        Assert.That(File.ReadAllText(optionsFile), Does.Contain("table_factory=PlainTable").And.Contain("user_key_len=34").And.Contain("allow_mmap_reads=true"));
    }

    private static StateId State(ulong blockNumber) => new(blockNumber, Keccak.Compute(blockNumber.ToString()));

    private static void Commit(IPrioritySlotStore store, StateId state, Action<IPrioritySlotStore.IWriter> write)
    {
        using IPrioritySlotStore.IWriter writer = store.BeginWrite();
        write(writer);
        writer.Commit(state);
    }

    private static void CommitRound(IPrioritySlotStore store, ValueHash256[] keys, ulong round) =>
        Commit(store, State(round), writer =>
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (round % 2 == 1 && i % 2 == 1) writer.Remove(0, keys[i]);
                else writer.Set(0, keys[i], round);
            }
        });

    private static void AssertSlots(IPrioritySlotStore.IReader reader, StateId state, params (byte Contract, ValueHash256 Key, UInt256? Value)[] expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.State, Is.EqualTo(state), "state");
            foreach ((byte contract, ValueHash256 key, UInt256? value) in expected)
            {
                bool found = reader.TryGet(contract, key, out UInt256 actual);
                Assert.That(found ? actual : (UInt256?)null, Is.EqualTo(value), $"contract {contract} key {key}");
            }
        }
    }

    private static void AssertNodes(IPrioritySlotStore.IReader reader, params (byte Contract, TreePath Path, string? Rlp)[] expected)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach ((byte contract, TreePath path, string? rlp) in expected)
            {
                byte[]? expectedRlp = rlp is null ? null : Bytes.FromHexString(rlp);
                Assert.That(reader.GetNode(contract, path), Is.EqualTo(expectedRlp), $"contract {contract} node {path}");

                StrongBox<byte[]?> passed = new();
                Assert.That(reader.TryGetNode(contract, path, static (span, box) => box.Value = span.ToArray(), passed), Is.EqualTo(rlp is not null), $"contract {contract} node {path} found");
                Assert.That(passed.Value, Is.EqualTo(expectedRlp), $"contract {contract} node {path} passed");
            }
        }
    }
}
