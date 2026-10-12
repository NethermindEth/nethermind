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
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.Proofs;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// One seeded random block stream through the world state of a node on the C# backend and of one on the Rust backend:
/// account and slot writes, self-destructs, resurrection of a destroyed address with new storage (CREATE2 after
/// SELFDESTRUCT), code, empty blocks and reorgs deeper than the compact size. Every block must reach the same state
/// root on both, after every flush their flat columns must be identical, and at the end the state reader, its proofs
/// and an overridable world scope must read the same through both, before and after the last flush.
/// </summary>
/// <remarks>
/// Each node runs the whole production graph of <see cref="TestNethermindModule"/>, so the trie is hashed by the C#
/// scope from nodes read through the Rust bundles and handed back to the Rust manager as snapshots.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class RustBackendEndToEndTests
{
    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;
    private static readonly IReleaseSpec Spec = Cancun.Instance;

    [Test]
    public async Task World_state_matches_the_csharp_backend([ValueSource(nameof(Layouts))] FlatLayout layout, [Values] bool carryForward)
    {
        RustNativeLibrary.Require();
        await using Node csharp = new(FlatDbBackend.CSharp, layout, carryForward);
        await using Node rust = new(FlatDbBackend.Rust, layout, carryForward);

        int seed = 20261014 + (int)layout * 2 + (carryForward ? 1 : 0);
        new BlockStream(new Random(seed), csharp, rust).Run();
    }

    /// <summary>A node's world state and flat DB.</summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly IContainer _container;

        public Node(FlatDbBackend backend, FlatLayout layout, bool carryForward)
        {
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(FlatManagerContainer.Config(backend, layout, carryForward)))
                .Build();
            WorldState = _container.Resolve<IMainProcessingContext>().WorldState;
            Manager = _container.Resolve<IFlatDbManager>();
            PersistenceManager = _container.Resolve<IPersistenceManager>();
            Db = _container.Resolve<IColumnsDb<FlatDbColumns>>();
        }

        public IWorldState WorldState { get; }

        public IFlatDbManager Manager { get; }

        public IPersistenceManager PersistenceManager { get; }

        public IColumnsDb<FlatDbColumns> Db { get; }

        /// <summary>Runs <paramref name="transactions"/> on <paramref name="parent"/>, each committed apart, and commits block <paramref name="number"/>.</summary>
        public Hash256 Commit(BlockHeader? parent, ulong number, IReadOnlyList<Action<IWorldState>> transactions)
        {
            Hash256 root;
            using (WorldState.BeginScope(parent))
            {
                foreach (Action<IWorldState> transaction in transactions)
                {
                    transaction(WorldState);
                    WorldState.Commit(Spec);
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

    private sealed class BlockStream(Random random, Node csharp, Node rust)
    {
        private const int BlockCount = 70;
        private const int CompactSize = 4;

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
                bool reorged = choice is >= 10 and < 20 && TryReorg();
                if (choice < 10) AddBlock(_chain.Count - 1, []);
                else if (!reorged) AddBlock(_chain.Count - 1, RandomTransactions());

                if (choice >= 92) Flush();
            }

            CompareReaders(_chain[^1], _chain[^3]);
            Flush();
            // The flush persisted the head and pruned the states below it.
            CompareReaders(_chain[^1]);
            Assert.That(_reorgs, Is.GreaterThan(0), "the stream reorged deeper than the compact size");
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
        private void AddBlock(int parentIndex, IReadOnlyList<Action<IWorldState>> transactions)
        {
            BlockHeader? parent = _chain[parentIndex];
            ulong number = parent is null ? 0 : parent.Number + 1;
            Hash256 expected = csharp.Commit(parent, number, transactions);
            Hash256 actual = rust.Commit(parent, number, transactions);
            Assert.That(actual, Is.EqualTo(expected), $"state root of block {number}");

            _chain.RemoveRange(parentIndex + 1, _chain.Count - parentIndex - 1);
            _chain.Add(Build.A.BlockHeader.WithNumber(number).WithStateRoot(expected).TestObject);
        }

        /// <summary>A block on an ancestor more than the compact size below the head, when both nodes still hold its state.</summary>
        private bool TryReorg()
        {
            int depth = CompactSize + 1 + random.Next(3);
            int parentIndex = _chain.Count - 1 - depth;
            if (parentIndex < 1) return false;

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
            csharp.Manager.FlushCache(CancellationToken.None);
            rust.Manager.FlushCache(CancellationToken.None);
            Assert.That(rust.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(csharp.PersistenceManager.GetCurrentPersistedStateId()));
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                Assert.That(FlatTestDbs.Dump(rust.Db, column), Is.EqualTo(FlatTestDbs.Dump(csharp.Db, column)), $"{column} after block {_chain[^1]?.Number}");
            }
        }

        private List<Action<IWorldState>> RandomTransactions() =>
            [.. Enumerable.Range(0, random.Next(1, 5)).Select(_ => RandomTransaction())];

        private Action<IWorldState> RandomTransaction()
        {
            Address address = _addresses[random.Next(_addresses.Length)];
            UInt256 slot = _slots[random.Next(_slots.Length)];
            UInt256 value = random.Next(4) == 0 ? UInt256.Zero : new UInt256((ulong)random.NextInt64(1, long.MaxValue));
            UInt256 amount = (ulong)random.Next(1, 1000);
            byte[] code = [0x60, (byte)random.Next(256), 0x00];
            return random.Next(6) switch
            {
                0 => state => state.AddToBalanceAndCreateIfNotExists(address, amount, Spec, out _),
                1 or 2 => SetSlot,
                3 => SelfDestruct,
                4 => Resurrect,
                _ => DeployCode,
            };

            void SetSlot(IWorldState state)
            {
                state.CreateAccountIfNotExists(address, amount);
                state.Set(new StorageCell(address, slot), value);
            }

            // SELFDESTRUCT of an account created in the same transaction (EIP-6780) clears it with its storage.
            void SelfDestruct(IWorldState state)
            {
                if (!state.AccountExists(address)) return;
                state.ClearStorage(address);
                state.DeleteAccount(address);
            }

            // CREATE2 at an address destroyed earlier: a fresh account with storage of its own.
            void Resurrect(IWorldState state)
            {
                if (state.AccountExists(address))
                {
                    state.ClearStorage(address);
                    state.DeleteAccount(address);
                    state.Commit(Spec);
                }

                state.CreateAccount(address, amount);
                state.Set(new StorageCell(address, slot), (UInt256)amount);
            }

            void DeployCode(IWorldState state)
            {
                state.CreateAccountIfNotExists(address, amount);
                state.InsertCode(address, ValueKeccak.Compute(code), code, Spec);
            }
        }
    }
}
