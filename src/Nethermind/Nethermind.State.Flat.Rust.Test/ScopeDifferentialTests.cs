// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The scopes of the world state of a node on the C# backend and of one on the Rust backend, which runs the forked
/// package, through <see cref="IWorldStateScopeProvider"/>: the same blocks of write batches, reads, hints and
/// commits, seeded or scripted, must give the same reads after every operation, the same root after every commit and
/// the same reads through the state reader after every block.
/// </summary>
/// <remarks>
/// <para>
/// A block commits once with changes, as the world state commits it, then possibly again with none, which keeps no
/// snapshot. Two commits of one block with different roots would chain two states of one block number, past which
/// neither backend persists or gathers.
/// </para>
/// <para>
/// With long finality no block is ever finalized, so the scopes of a chain longer than the in-memory window read
/// through the persisted-snapshot tier its older states are converted into.
/// </para>
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class ScopeDifferentialTests
{
    private static readonly Address[] Addresses = [.. Enumerable.Range(1, 5).Select(static index => new Address($"0xcccccccc{index:x32}"))];
    private static readonly UInt256[] Slots = [1, 2, 3, 255, UInt256.MaxValue];
    private static readonly Address A = Addresses[0];
    private static readonly Address B = Addresses[1];

    [Test]
    public async Task Fork_scopes_match_the_original_scopes([Values] bool deferStorageTrieCommit, [Range(0, 3)] int seed, [Values] bool longFinality)
    {
        RustNativeLibrary.Require();
        await using Pair pair = new(deferStorageTrieCommit, longFinality);
        Random random = new(20261021 + seed * 2 + (deferStorageTrieCommit ? 1 : 0));
        for (int block = 0; block < 24; block++)
        {
            // Mostly on the head, sometimes on a block a few back.
            int parent = pair.Chain.Count - 1 - (pair.Chain.Count > 4 && random.Next(5) == 0 ? random.Next(1, 4) : 0);
            pair.Block(parent, scope =>
            {
                for (int batch = random.Next(1, 4); batch > 0; batch--)
                {
                    scope.Batch(RandomWrites(random));
                    if (random.Next(3) == 0) scope.Hint(Pick(random, Addresses), RandomAccount(random));
                    if (random.Next(3) == 0) scope.Read(Pick(random, Addresses));
                }

                if (random.Next(3) == 0) scope.Commit();
            });
        }

        if (longFinality) Assert.That(pair.PersistedSnapshotCounts, Is.All.Positive, "both backends read through a tier");
    }

    /// <summary>The scenario with blocks enough for long finality to convert some into the tier.</summary>
    private const string ManyBlocks = "storage nodes read through the snapshots of the bundle, over many blocks and commits";

    /// <summary>The scripted cases of the original scope's tests, through both backends; with long finality only <see cref="ManyBlocks"/>.</summary>
    [TestCaseSource(nameof(Scenarios))]
    public async Task Fork_scopes_match_the_original_scopes_in(string scenario, Action<Pair> run, bool deferStorageTrieCommit, bool longFinality)
    {
        RustNativeLibrary.Require();
        await using Pair pair = new(deferStorageTrieCommit, longFinality);
        run(pair);
        if (longFinality) Assert.That(pair.PersistedSnapshotCounts, Is.All.Positive, "both backends read through a tier");
    }

    private static IEnumerable<TestCaseData> Scenarios()
    {
        (string Name, Action<Pair> Run)[] scenarios =
        [
            ("shadowing of accounts and slots by later blocks", static pair =>
            {
                pair.Block(0, static scope => scope.Batch([Write.Account(A, 1, 10), Write.SetSlot(A, 1, 11), Write.SetSlot(A, 2, 12)]));
                pair.Block(1, static scope => scope.Batch([Write.Account(A, 2, 20), Write.SetSlot(A, 1, 21)]));
                pair.Block(2, static scope => scope.Batch([Write.Account(B, 1, 1)]));
            }),
            ("a read promoted into the block and its write after the commit", static pair =>
            {
                pair.Block(0, static scope => scope.Batch([Write.Account(A, 1, 10)]));
                pair.Block(1, static scope =>
                {
                    scope.Read(A);
                    scope.Hint(B, null);
                    scope.Batch([Write.Account(A, 2, 20)]);
                    scope.Commit();
                    scope.Read(A);
                    scope.Read(B);
                });
            }),
            ("a hint never overwrites an account written in the block", static pair =>
            {
                pair.Block(0, static scope =>
                {
                    scope.Batch([Write.Account(A, 1, 10)]);
                    scope.Hint(A, new Account(9, 99));
                    scope.Hint(B, new Account(3, 33));
                    scope.Read(A);
                    scope.Read(B);
                });
            }),
            ("a self-destruct hides the slots written before it, above and below the bundle", static pair =>
            {
                pair.Block(0, static scope => scope.Batch([Write.Account(A, 1, 10), Write.SetSlot(A, 1, 11), Write.SetSlot(A, 2, 12)]));
                for (int block = 1; block < 6; block++) pair.Block(block, static scope => scope.Batch([Write.Account(B, (ulong)scope.Number, 1)]));
                pair.Block(6, static scope => scope.Batch([Write.Clear(A), Write.Account(A, 2, 20), Write.SetSlot(A, 3, 33)]));
                pair.Block(7, static scope => scope.Batch([Write.SetSlot(A, 1, 41), Write.Account(A, 3, 30)]));
            }),
            ("a deleted account comes back without its storage", static pair =>
            {
                pair.Block(0, static scope => scope.Batch([Write.Account(A, 1, 10), Write.SetSlot(A, 1, 11)]));
                pair.Block(1, static scope => scope.Batch([Write.Delete(A)]));
                pair.Block(2, static scope => scope.Batch([Write.Account(A, 1, 1), Write.SetSlot(A, 2, 22)]));
            }),
            ("a self-destruct and new storage in one block, cleared then written again in a later batch", static pair =>
            {
                pair.Block(0, static scope => scope.Batch([Write.Account(A, 1, 10), Write.SetSlot(A, 1, 11), Write.SetSlot(A, 2, 12)]));
                pair.Block(1, static scope =>
                {
                    scope.Batch([Write.SetSlot(A, 3, 13), Write.Account(A, 1, 10)]);
                    scope.Batch([Write.Clear(A), Write.Account(A, 2, 20), Write.SetSlot(A, 1, 51)]);
                    scope.Batch([Write.SetSlot(A, 4, 54), Write.Account(A, 3, 30)]);
                });
            }),
            (ManyBlocks, static pair =>
            {
                for (int block = 0; block < 12; block++)
                {
                    pair.Block(block, static scope => scope.Batch(
                        [.. Addresses.Select(address => Write.Account(address, (ulong)scope.Number, 1)), .. Slots.Select(slot => Write.SetSlot(A, slot, (ulong)scope.Number + 1))]));
                }
            }),
            ("a commit of nothing new keeps no snapshot", static pair =>
            {
                pair.Block(0, static scope => scope.Batch([Write.Account(A, 1, 10)]));
                pair.Block(1, static scope =>
                {
                    scope.Batch([Write.SetSlot(A, 1, 1), Write.Account(A, 1, 10)]);
                    scope.Commit();
                    scope.Commit();
                });
                pair.Block(2, static scope => scope.Read(A));
            }),
            ("a branch on an older block", static pair =>
            {
                for (int block = 0; block < 6; block++) pair.Block(block, static scope => scope.Batch([Write.Account(A, (ulong)scope.Number, 1), Write.SetSlot(A, 1, (ulong)scope.Number)]));
                pair.Block(2, static scope => scope.Batch([Write.Clear(A), Write.Account(A, 9, 9)]));
            }),
        ];

        foreach ((string name, Action<Pair> run) in scenarios)
        {
            foreach (bool defer in new[] { false, true })
            {
                foreach (bool longFinality in name == ManyBlocks ? new[] { false, true } : [false])
                {
                    yield return new TestCaseData(name, run, defer, longFinality).SetArgDisplayNames(name, $"defer {defer}", $"long finality {longFinality}");
                }
            }
        }
    }

    private static Write[] RandomWrites(Random random) =>
    [
        .. Enumerable.Range(0, random.Next(1, 6)).Select(_ =>
        {
            Address address = Pick(random, Addresses);
            return random.Next(10) switch
            {
                0 => Write.Clear(address),
                1 => Write.Delete(address),
                < 5 => Write.SetSlot(address, Pick(random, Slots), random.Next(4) == 0 ? 0UL : (ulong)random.Next(1, 1000)),
                _ => Write.Account(address, (ulong)random.Next(5), (ulong)random.Next(1000)),
            };
        }),
    ];

    private static Account RandomAccount(Random random) => new((ulong)random.Next(5), (UInt256)random.Next(1000));

    private static T Pick<T>(Random random, T[] items) => items[random.Next(items.Length)];

    /// <summary>A write of a batch: an account, a slot, a storage clear or an account deletion.</summary>
    /// <remarks>A slot or a clear also writes its account, as the world state does, keeping it if it exists.</remarks>
    public sealed record Write(Address Address, Account? NewAccount, UInt256? Slot, UInt256 Value, bool ClearsStorage, bool Deletes)
    {
        public static Write Account(Address address, ulong nonce, ulong balance) => new(address, new Account(nonce, balance), null, default, false, false);

        public static Write SetSlot(Address address, UInt256 slot, ulong value) => new(address, null, slot, value, false, false);

        public static Write Clear(Address address) => new(address, null, null, default, true, false);

        public static Write Delete(Address address) => new(address, null, null, default, false, true);
    }

    /// <summary>A node of each backend over a RocksDB of its own, building the same chain.</summary>
    public sealed class Pair(bool deferStorageTrieCommit, bool longFinality) : IAsyncDisposable
    {
        private readonly Node _original = new(FlatDbBackend.CSharp, deferStorageTrieCommit, longFinality);
        private readonly Node _fork = new(FlatDbBackend.Rust, deferStorageTrieCommit, longFinality);

        /// <summary>The headers of the blocks built, the empty state first.</summary>
        public List<BlockHeader?> Chain { get; } = [null];

        /// <summary>Builds a block on <c>Chain[parent]</c> with <paramref name="body"/> on both, dropping the chain above the parent.</summary>
        public void Block(int parent, Action<DualScope> body)
        {
            BlockHeader? parentHeader = Chain[parent];
            ulong number = parentHeader is null ? 0 : parentHeader.Number + 1;
            using DualScope scope = new(_original.Begin(parentHeader), _fork.Begin(parentHeader), number);
            body(scope);
            Hash256 root = scope.Commit();

            BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithStateRoot(root).TestObject;
            Chain.RemoveRange(parent + 1, Chain.Count - parent - 1);
            Chain.Add(header);
            _original.Persist(header);
            _fork.Persist(header);
            Assert.That(_fork.Reads(header), Is.EqualTo(_original.Reads(header)), $"state reader at block {number}");
        }

        /// <summary>The snapshots in the persisted-snapshot tier of each backend.</summary>
        public long[] PersistedSnapshotCounts => [_original.PersistedSnapshotCount, _fork.PersistedSnapshotCount];

        public async ValueTask DisposeAsync()
        {
            await _original.DisposeAsync();
            await _fork.DisposeAsync();
        }
    }

    /// <summary>A scope of each backend at the same state, every operation run on both and its reads compared.</summary>
    public sealed class DualScope(IWorldStateScopeProvider.IScope original, IWorldStateScopeProvider.IScope fork, ulong number) : IDisposable
    {
        private int _operation;

        public ulong Number => number;

        public void Batch(Write[] writes)
        {
            Apply(original, writes);
            Apply(fork, writes);
            Compare($"batch {++_operation}");
        }

        public void Read(Address address)
        {
            Assert.That(Render(fork.Get(address)), Is.EqualTo(Render(original.Get(address))), $"get {address}");
            Compare($"get {++_operation}");
        }

        public void Hint(Address address, Account? account)
        {
            original.HintGet(address, account);
            fork.HintGet(address, account);
            Compare($"hint {++_operation}");
        }

        public Hash256 Commit()
        {
            original.UpdateRootHash();
            fork.UpdateRootHash();
            original.Commit(number);
            fork.Commit(number);
            Assert.That(fork.RootHash, Is.EqualTo(original.RootHash), $"root after commit {++_operation} of block {number}");
            Compare($"commit {_operation}");
            return original.RootHash;
        }

        public void Dispose()
        {
            original.Dispose();
            fork.Dispose();
        }

        private static void Apply(IWorldStateScopeProvider.IScope scope, Write[] writes)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(writes.Length);
            foreach (IGrouping<Address, Write> account in writes.GroupBy(static write => write.Address))
            {
                Account current = scope.Get(account.Key) ?? new Account(0, 0);
                if (account.Any(static write => write.Deletes))
                {
                    // As a self-destruct deletes it: its storage goes too.
                    using (IWorldStateScopeProvider.IStorageWriteBatch cleared = batch.CreateStorageWriteBatch(account.Key, 0)) cleared.Clear();
                    batch.Set(account.Key, null);
                    continue;
                }

                // An account keeps the root of its storage, which is cleared as new only when it holds none.
                foreach (Write write in account.Where(static write => write.NewAccount is not null)) current = write.NewAccount!.WithChangedStorageRoot(current.StorageRoot);
                batch.Set(account.Key, current);
                Write[] storage = [.. account.Where(static write => write.ClearsStorage || write.Slot is not null)];
                if (storage.Length == 0) continue;

                using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(account.Key, storage.Length);
                if (storage.Any(static write => write.ClearsStorage)) storageBatch.Clear();
                foreach (Write write in storage.Where(static write => write.Slot is not null)) storageBatch.Set(write.Slot!.Value, write.Value);
            }
        }

        private void Compare(string after) =>
            Assert.That(Reads(fork), Is.EqualTo(Reads(original)), $"reads after {after} of block {number}");

        private static List<string> Reads(IWorldStateScopeProvider.IScope scope)
        {
            List<string> reads = [];
            foreach (Address address in Addresses)
            {
                reads.Add($"account {address} {Render(scope.Get(address))}");
                IWorldStateScopeProvider.IStorageTree storage = scope.CreateStorageTree(address);
                foreach (UInt256 slot in Slots)
                {
                    storage.Get(slot, out UInt256 value);
                    reads.Add($"slot {address} {slot} {value}");
                }
            }

            return reads;
        }
    }

    private static string Render(Account? account) => account is null ? "null" : $"{account.Nonce} {account.Balance} {account.StorageRoot}";

    private sealed class Node : IAsyncDisposable
    {
        private readonly IContainer _container;
        private readonly IWorldStateScopeProvider _provider;
        private readonly IStateReader _reader;
        private readonly IPersistenceManager _persistenceManager;
        private readonly TempPath? _tierPath;

        public Node(FlatDbBackend backend, bool deferStorageTrieCommit, bool longFinality)
        {
            FlatDbConfig config = FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality);
            config.DeferStorageTrieCommit = deferStorageTrieCommit;
            ContainerBuilder builder = new ContainerBuilder().AddModule(new TestNethermindModule(config));
            if (longFinality)
            {
                _tierPath = TempPath.GetTempDirectory();
                LongFinalityNode.Enable(builder, backend, _tierPath.Path);
            }

            _container = builder.Build();
            IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
            _provider = worldStateManager.GlobalWorldState;
            _reader = worldStateManager.GlobalStateReader;
            _persistenceManager = _container.Resolve<IPersistenceManager>();
        }

        public IWorldStateScopeProvider.IScope Begin(BlockHeader? parent)
        {
            Assert.That(_provider.TryBeginScope(parent, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.True, $"scope at {parent?.Number}");
            return scope!;
        }

        /// <summary>Persists what the finality allows, as both backends then hold the same states.</summary>
        public void Persist(BlockHeader header) => FlatManagerContainer.PersistUpTo(_persistenceManager, new StateId(header));

        public List<string> Reads(BlockHeader header)
        {
            List<string> reads = [];
            foreach (Address address in Addresses)
            {
                reads.Add(_reader.TryGetAccount(header, address, out AccountStruct account)
                    ? $"account {address} {account.Nonce} {account.Balance} {account.StorageRoot}"
                    : $"account {address} none");
                foreach (UInt256 slot in Slots)
                {
                    _reader.GetStorage(header, address, slot, out UInt256 value);
                    reads.Add($"slot {address} {slot} {value}");
                }
            }

            return reads;
        }

        public long PersistedSnapshotCount => NativeTier.PersistedSnapshotCount(_container);

        public async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
            _tierPath?.Dispose();
        }
    }
}
