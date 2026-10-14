// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// One seeded random block stream through the world state of a node on the C# backend and of one on the Rust backend:
/// a pre-Byzantium segment hashing the state after every transaction, account and slot writes, self-destructs,
/// resurrection of a destroyed address with new storage (CREATE2 after SELFDESTRUCT), code, empty blocks, reorgs deeper
/// than the compact size, a branch of 2 blocks left for the chain it forked from, and a branch of 65 blocks. Every
/// block must reach the same state root on both, after every flush their flat columns must be identical, and at the
/// end the state reader, its proofs and an overridable world scope must read the same through both, before and after
/// the last flush.
/// </summary>
/// <remarks>
/// <para>
/// Each node runs the whole production graph of <see cref="TestNethermindModule"/>, with the trie warmer and the
/// early storage apply on, so the trie is hashed by the scope from nodes read through the bundles of its backend; on
/// the Rust backend those are the forked package's, whose snapshots are native.
/// </para>
/// <para>
/// With long finality, finality never comes, so the states past the in-memory window are converted into the
/// persisted-snapshot tier; the long branch is rooted in the tier, the readers also read at states only the tier
/// holds, and the stream ends with blocks past the last flush, so the converted snapshots both nodes leave in the tier
/// are compared once they are disposed (<see cref="PersistedDump"/>).
/// </para>
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class RustBackendEndToEndTests
{
    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;
    private static readonly IReleaseSpec PreByzantium = Homestead.Instance;
    private static readonly IReleaseSpec Spec = Cancun.Instance;

    [Test]
    public async Task World_state_matches_the_csharp_backend(
        [ValueSource(nameof(Layouts))] FlatLayout layout,
        [Values] bool carryForward,
        [Values] bool deferStorageTrieCommit,
        [Values] bool longFinality)
    {
        RustNativeLibrary.Require();
        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        await using (Node csharp = new(FlatDbBackend.CSharp, layout, carryForward, deferStorageTrieCommit, longFinality ? csharpPath.Path : null),
            rust = new(FlatDbBackend.Rust, layout, carryForward, deferStorageTrieCommit, longFinality ? rustPath.Path : null))
        {
            int seed = 20261014 + (int)layout * 4 + (carryForward ? 2 : 0) + (deferStorageTrieCommit ? 1 : 0) + (longFinality ? 100 : 0);
            new BlockStream(new Random(seed), csharp, rust, longFinality).Run();
        }

        if (!longFinality) return;

        FlatDbConfig config = FlatManagerContainer.Config(FlatDbBackend.CSharp, layout, carryForward, maxInMemorySnapshotBytes: 0, longFinality: true);
        List<string> expected = PersistedDump.Take(csharpPath.Path, config);
        Assert.That(expected, Is.Not.Empty, "the tier holds converted snapshots at the end");
        Assert.That(PersistedDump.Take(rustPath.Path, config), Is.EqualTo(expected), "the converted snapshots of the tier after disposal");
    }

    /// <summary>A node's world state and flat DB.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly IContainer _container;

        /// <param name="longFinalityPath">Where the node keeps its persisted-snapshot tier with long finality on, or null for long finality off.</param>
        public Node(FlatDbBackend backend, FlatLayout layout, bool carryForward, bool deferStorageTrieCommit, string? longFinalityPath)
        {
            FlatDbConfig config = FlatManagerContainer.Config(backend, layout, carryForward, maxInMemorySnapshotBytes: 0, longFinality: longFinalityPath is not null);
            config.DeferStorageTrieCommit = deferStorageTrieCommit;
            // The flat module picks the warmer registration from this count when it loads.
            config.TrieWarmerWorkerCount = 2;
            ContainerBuilder builder = new ContainerBuilder()
                .AddModule(new TestNethermindModule(config))
                // The test module turns the trie warmer off; the stream runs with it, and with the early storage apply.
                .Intercept<IFlatDbConfig>(static flatDbConfig =>
                {
                    flatDbConfig.TrieWarmerWorkerCount = 2;
                    flatDbConfig.ApplyStorageWritesOnIdleThread = true;
                });
            if (longFinalityPath is not null) LongFinalityNode.Enable(builder, backend, longFinalityPath);
            _container = builder.Build();
            Assert.That(_container.Resolve<ITrieWarmer>(), backend == FlatDbBackend.Rust ? Is.TypeOf<Fork.TrieWarmer>() : Is.TypeOf<TrieWarmer>());
            WorldState = _container.Resolve<IMainProcessingContext>().WorldState;
            Manager = _container.Resolve<IFlatDbManager>();
            PersistenceManager = _container.Resolve<IPersistenceManager>();
            Db = _container.Resolve<IColumnsDb<FlatDbColumns>>();
        }

        public IWorldState WorldState { get; }

        public IFlatDbManager Manager { get; }

        public IPersistenceManager PersistenceManager { get; }

        public IColumnsDb<FlatDbColumns> Db { get; }

        /// <summary>The snapshots in the node's persisted-snapshot tier.</summary>
        public long PersistedSnapshotCount => NativeTier.PersistedSnapshotCount(_container);

        /// <summary>Changes whenever the persisted-snapshot compactor publishes a merge or a prune drops a snapshot of the tier.</summary>
        public long TierFingerprint => NativeTier.Fingerprint(_container);

        /// <summary>
        /// Runs <paramref name="transactions"/> on <paramref name="parent"/>, each committed apart, and commits block
        /// <paramref name="number"/>; before Byzantium the state root is hashed after every transaction.
        /// </summary>
        public Hash256 Commit(BlockHeader? parent, ulong number, IReleaseSpec spec, IReadOnlyList<Action<IWorldState, IReleaseSpec>> transactions)
        {
            Hash256 root;
            using (WorldState.BeginScope(parent))
            {
                foreach (Action<IWorldState, IReleaseSpec> transaction in transactions)
                {
                    transaction(WorldState, spec);
                    WorldState.Commit(spec);
                    if (!spec.IsEip658Enabled) WorldState.RecalculateStateRoot();
                }

                WorldState.CommitTree(number);
                root = WorldState.StateRoot;
            }

            // The persistence of the C# backend is asynchronous; running it here keeps both nodes retaining the same states.
            FlatManagerContainer.PersistUpTo(PersistenceManager, new StateId(number, root));
            return root;
        }

        /// <summary>What <see cref="FlatStateReader"/>, its account proofs and a <see cref="IOverridableWorldScope"/> read at <paramref name="header"/>.</summary>
        public List<string> Reads(BlockHeader header, Address[] addresses, UInt256[] slots)
        {
            List<string> reads = [];
            IWorldStateManager worldStateManager = _container.Resolve<IWorldStateManager>();
            using IOverridableWorldScope overridable = worldStateManager.CreateOverridableWorldScope();
            foreach ((string name, IStateReader reader) in new[] { ("global", worldStateManager.GlobalStateReader), ("overridable", overridable.GlobalStateReader) })
            {
                foreach (Address address in addresses)
                {
                    reads.Add(reader.TryGetAccount(header, address, out AccountStruct account)
                        ? $"{name} account {address} {account.Nonce} {account.Balance} {account.StorageRoot} {account.CodeHash}"
                        : $"{name} account {address} none");
                    foreach (UInt256 slot in slots)
                    {
                        reader.GetStorage(header, address, slot, out UInt256 value);
                        reads.Add($"{name} slot {address} {slot} {value}");
                    }
                }
            }

            foreach (Address address in addresses)
            {
                AccountProofCollector collector = new(address, slots);
                worldStateManager.GlobalStateReader.RunTreeVisitor(collector, header);
                AccountProof proof = collector.BuildResult();
                reads.Add($"proof {address} {string.Join(",", proof.Proof.Select(static item => item.ToHexString()))}");
                reads.AddRange(proof.StorageProofs.Select(storage => $"storage proof {address} {string.Join(",", storage.Proof.Select(static item => item.ToHexString()))}"));
            }

            return reads;
        }

        public ValueTask DisposeAsync() => _container.DisposeAsync();
    }

    /// <param name="longFinality">Whether the nodes convert the states past their in-memory window into a tier.</param>
    private sealed class BlockStream(Random random, Node csharp, Node rust, bool longFinality)
    {
        private const int PreByzantiumBlocks = 10;
        private const int BlockCount = 70;
        private const int CompactSize = 4;
        private const int LongBranch = 65;
        /// <summary>The base snapshots kept in memory with long finality; deeper states are in the tier.</summary>
        private const int InMemoryBases = 8;

        private readonly Address[] _addresses = [.. Enumerable.Range(1, 8).Select(static i => new Address($"0x{i:x40}"))];
        private readonly UInt256[] _slots = [0, 1, 7, UInt256.MaxValue];
        private readonly List<BlockHeader?> _chain = [];
        private int _reorgs;

        public void Run()
        {
            _chain.Add(null);
            for (int block = 0; block < BlockCount; block++)
            {
                int choice = random.Next(100);
                bool reorged = block >= PreByzantiumBlocks && choice is >= 10 and < 20 && TryReorg();
                if (choice < 10) AddBlock(_chain.Count - 1, []);
                else if (!reorged) AddBlock(_chain.Count - 1, RandomTransactions());

                if (choice >= 92) Flush();
            }

            // Blocks enough above the last flush for the branches below to fork within the reorg window, or in the tier.
            for (int block = 0; block < 2 * CompactSize + (longFinality ? 2 * InMemoryBases : 0); block++) AddBlock(_chain.Count - 1, RandomTransactions());
            ShortBranchLeftBehind();
            LongBranchBecomesTheChain();
            CompareReaders(_chain[^1], _chain[^3]);
            if (longFinality)
            {
                Assert.That(rust.PersistedSnapshotCount, Is.Positive, "the Rust node holds a tier");
                Assert.That(csharp.PersistedSnapshotCount, Is.Positive, "the C# node holds a tier");
                // States only the tier holds: deeper than the in-memory window, above the backstop.
                CompareReaders(_chain[^(2 * InMemoryBases)], _chain[^(3 * InMemoryBases)]);
            }

            Flush();
            // The flush persisted the head and pruned the states below it.
            CompareReaders(_chain[^1]);
            Assert.That(_reorgs, Is.GreaterThan(0), "the stream reorged deeper than the compact size");
            if (!longFinality) return;

            // Blocks past the flush, for the nodes to leave a tier behind.
            for (int block = 0; block < 3 * InMemoryBases; block++) AddBlock(_chain.Count - 1, RandomTransactions());
            CompareReaders(_chain[^1], _chain[^(2 * InMemoryBases)]);
        }

        /// <summary>A branch of 2 blocks on a block a few back, after which the chain it forked from goes on.</summary>
        private void ShortBranchLeftBehind()
        {
            List<BlockHeader?> chain = [.. _chain];
            int parentIndex = _chain.Count - 4;
            AddBlock(parentIndex, RandomTransactions());
            AddBlock(_chain.Count - 1, RandomTransactions());
            CompareReaders(_chain[^1]);

            _chain.Clear();
            _chain.AddRange(chain);
            AddBlock(_chain.Count - 1, RandomTransactions());
        }

        /// <summary>
        /// A branch of <see cref="LongBranch"/> blocks on a block deeper than the compact size, which becomes the chain;
        /// with long finality, on a block only the tier holds.
        /// </summary>
        private void LongBranchBecomesTheChain()
        {
            int parentIndex = _chain.Count - 1 - (longFinality ? InMemoryBases + 4 : CompactSize + 2);
            _reorgs++;
            AddBlock(parentIndex, RandomTransactions());
            for (int block = 1; block < LongBranch; block++) AddBlock(_chain.Count - 1, RandomTransactions());
        }

        /// <summary>The state reader, its proofs and an overridable scope read the same at every one of <paramref name="headers"/>.</summary>
        private void CompareReaders(params BlockHeader?[] headers)
        {
            foreach (BlockHeader? header in headers)
            {
                Assert.That(rust.Reads(header!, _addresses, _slots), Is.EqualTo(csharp.Reads(header!, _addresses, _slots)), $"reads at block {header!.Number}");
            }
        }

        /// <summary>Adds a block on <c>_chain[parentIndex]</c>, dropping the chain above it.</summary>
        private void AddBlock(int parentIndex, IReadOnlyList<Action<IWorldState, IReleaseSpec>> transactions)
        {
            BlockHeader? parent = _chain[parentIndex];
            ulong number = parent is null ? 0 : parent.Number + 1;
            IReleaseSpec spec = number < PreByzantiumBlocks ? PreByzantium : Spec;
            Hash256 expected = csharp.Commit(parent, number, spec, transactions);
            Hash256 actual = rust.Commit(parent, number, spec, transactions);
            Assert.That(actual, Is.EqualTo(expected), $"state root of block {number}");

            _chain.RemoveRange(parentIndex + 1, _chain.Count - parentIndex - 1);
            _chain.Add(Build.A.BlockHeader.WithNumber(number).WithStateRoot(expected).TestObject);
        }

        /// <summary>A block on an ancestor more than the compact size below the head, when both nodes still hold its state.</summary>
        private bool TryReorg()
        {
            int depth = CompactSize + 1 + random.Next(3);
            int parentIndex = _chain.Count - 1 - depth;
            // Never onto genesis: its empty state is held by every node, also once a flush persisted past it.
            if (parentIndex < 2) return false;

            BlockHeader? parent = _chain[parentIndex];
            bool retained = csharp.WorldState.HasStateForBlock(parent);
            Assert.That(rust.WorldState.HasStateForBlock(parent), Is.EqualTo(retained), $"state of block {parent?.Number}");
            if (!retained) return false;

            _reorgs++;
            AddBlock(parentIndex, RandomTransactions());
            return true;
        }

        private void Flush()
        {
            if (longFinality) NativeTier.WaitUntilSettled(() => csharp.TierFingerprint, () => rust.TierFingerprint);
            csharp.Manager.FlushCache(CancellationToken.None);
            rust.Manager.FlushCache(CancellationToken.None);
            Assert.That(rust.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(csharp.PersistenceManager.GetCurrentPersistedStateId()));
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                Assert.That(FlatTestDbs.Dump(rust.Db, column), Is.EqualTo(FlatTestDbs.Dump(csharp.Db, column)), $"{column} after block {_chain[^1]?.Number}");
            }
        }

        private List<Action<IWorldState, IReleaseSpec>> RandomTransactions() =>
            [.. Enumerable.Range(0, random.Next(1, 5)).Select(_ => RandomTransaction())];

        private Action<IWorldState, IReleaseSpec> RandomTransaction()
        {
            Address address = _addresses[random.Next(_addresses.Length)];
            UInt256 slot = _slots[random.Next(_slots.Length)];
            UInt256 value = random.Next(4) == 0 ? UInt256.Zero : new UInt256((ulong)random.NextInt64(1, long.MaxValue));
            UInt256 amount = (ulong)random.Next(1, 1000);
            byte[] code = [0x60, (byte)random.Next(256), 0x00];
            return random.Next(6) switch
            {
                0 => (state, spec) => state.AddToBalanceAndCreateIfNotExists(address, amount, spec, out _),
                1 or 2 => SetSlot,
                3 => SelfDestruct,
                4 => Resurrect,
                _ => DeployCode,
            };

            void SetSlot(IWorldState state, IReleaseSpec spec)
            {
                state.CreateAccountIfNotExists(address, amount);
                state.Set(new StorageCell(address, slot), value);
            }

            // SELFDESTRUCT of an account created in the same transaction (EIP-6780) clears it with its storage.
            void SelfDestruct(IWorldState state, IReleaseSpec spec)
            {
                if (!state.AccountExists(address)) return;
                state.ClearStorage(address);
                state.DeleteAccount(address);
            }

            // CREATE2 at an address destroyed earlier: a fresh account with storage of its own.
            void Resurrect(IWorldState state, IReleaseSpec spec)
            {
                if (state.AccountExists(address))
                {
                    state.ClearStorage(address);
                    state.DeleteAccount(address);
                    state.Commit(spec);
                }

                state.CreateAccount(address, amount);
                state.Set(new StorageCell(address, slot), (UInt256)amount);
            }

            void DeployCode(IWorldState state, IReleaseSpec spec)
            {
                state.CreateAccountIfNotExists(address, amount);
                state.InsertCode(address, ValueKeccak.Compute(code), code, spec);
            }
        }
    }
}
