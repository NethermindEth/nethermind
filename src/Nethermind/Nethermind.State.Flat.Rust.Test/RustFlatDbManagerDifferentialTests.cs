// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// One seeded random stream of snapshots, forks, reorgs deeper than the compact size, finality, flushes and drops
/// through a C# and a Rust flat DB manager, each over a RocksDB of its own: every native snapshot must hold what the
/// C# one holds, and after every step both must hold the same states, read the same accounts, slots and trie nodes
/// through their bundles, have persisted to the same state and marked the same C# trie nodes persisted; after every
/// flush their columns must be byte-identical.
/// </summary>
/// <remarks>
/// The C# manager persists on a background task, so every step ends by running <see cref="IPersistenceManager.AddToPersistence"/>
/// on both until it persists nothing more: a background persist can then only find nothing more to do, which keeps both
/// in step.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class RustFlatDbManagerDifferentialTests
{
    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;

    [Test]
    public async Task Rust_manager_matches_the_csharp_manager_in_lockstep(
        [ValueSource(nameof(Layouts))] FlatLayout layout,
        [Values] bool carryForward,
        [Values] bool byteBudget)
    {
        RustNativeLibrary.Require();
        if (byteBudget)
        {
            Assert.Ignore("Where a forced persist stops depends on timing on both managers: the background persist job of one block, " +
                "run while the next block is added and before it is compacted, persists below that block's compaction boundary");
        }

        using TempPath csharpPath = TempPath.GetTempDirectory();
        using TempPath rustPath = TempPath.GetTempDirectory();
        TestFinality finality = new();
        ulong budget = byteBudget ? 48 * 1024UL : 0;
        await using FlatManagerContainer csharp = new(FlatManagerContainer.Config(FlatDbBackend.CSharp, layout, carryForward, budget), csharpPath.Path, finality);
        await using FlatManagerContainer rust = new(FlatManagerContainer.Config(FlatDbBackend.Rust, layout, carryForward, budget), rustPath.Path, finality);

        int seed = 20261013 + (int)layout * 4 + (carryForward ? 2 : 0) + (byteBudget ? 1 : 0);
        new Lockstep(new Random(seed), finality, csharp, rust).Run();
    }

    /// <summary>The writes of one block, applied to a C# snapshot of each manager.</summary>
    internal sealed record BlockPlan(
        (Address Address, Account? Account)[] Accounts,
        (Address Address, UInt256 Slot, UInt256? Value)[] Slots,
        (Address Address, bool IsNew)[] SelfDestructs,
        (TreePath Path, Hash256 Hash, byte[]? Rlp)[] StateNodes,
        (Hash256 Owner, TreePath Path, Hash256 Hash, byte[]? Rlp)[] StorageNodes)
    {
        public static readonly BlockPlan Empty = new([], [], [], [], []);

        /// <summary>Builds the snapshot of the plan in <paramref name="container"/> and adds it.</summary>
        /// <inheritdoc cref="FlatArm.Add(BlockPlan, in StateId, in StateId)"/>
        public TrieNode[] AddTo(FlatManagerContainer container, StateId from, StateId to) => container.Arm.Add(this, from, to);
    }

    /// <summary>What a scope reads through a gathered bundle.</summary>
    /// <remarks>
    /// The bundle is gathered with reader flags, so neither manager serves it from its bundle cache: a cached bundle reads
    /// the persistence as it was when the bundle was built, which a hash-only placeholder falls through to.
    /// </remarks>
    internal static class BundleReads
    {
        public static List<string> Read(FlatArm manager, in StateId state, Address[] addresses, UInt256[] slots, TreePath[] paths, Hash256[] owners)
        {
            List<string> reads = [];
            IBundleView bundle;
            try
            {
                bundle = manager.GatherReadOnly(state, ReaderFlags.Sync);
            }
            catch (Exception exception)
            {
                return [$"{exception.GetType().Name}: {exception.Message}"];
            }

            using (bundle)
            {
                foreach (Address address in addresses)
                {
                    reads.Add($"account {address} {Render(bundle.GetAccount(address))}");
                    int selfDestructIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
                    foreach (UInt256 slot in slots)
                    {
                        reads.Add($"slot {address} {slot} {bundle.GetSlot(address, slot, selfDestructIdx)?.ToString() ?? "null"}");
                    }
                }

                foreach (TreePath path in paths)
                {
                    reads.Add($"state node {path} {bundle.LoadStateRlp(path)?.ToHexString() ?? "null"}");
                }

                foreach (Hash256 owner in owners)
                {
                    foreach (TreePath path in paths)
                    {
                        reads.Add($"storage node {owner} {path} {bundle.LoadStorageRlp(owner, path)?.ToHexString() ?? "null"}");
                    }
                }
            }

            // Through a scope's shared bundle with the slot filter, as read-only execution reads.
            using IBundleView filtered = manager.GatherScope(state, ResourcePool.Usage.ReadOnlyProcessingEnv, filterInMemorySlotReads: true);
            foreach (Address address in addresses)
            {
                int selfDestructIdx = filtered.DetermineSelfDestructSnapshotIdx(address);
                foreach (UInt256 slot in slots)
                {
                    reads.Add($"filtered slot {address} {slot} {filtered.GetSlot(address, slot, selfDestructIdx)?.ToString() ?? "null"}");
                }
            }

            return reads;
        }

        private static string Render(Account? account) =>
            account is null ? "null" : $"{account.Nonce} {account.Balance} {account.StorageRoot} {account.CodeHash}";
    }

    private sealed class Lockstep(Random random, TestFinality finality, FlatManagerContainer csharp, FlatManagerContainer rust)
    {
        private const int StepCount = 150;
        private const ulong CompactSize = 4;

        private readonly Address[] _addresses =
        [
            new("0xaaaaaaaa00000000000000000000000000000001"),
            new("0xaaaaaaaa00000000000000000000000000000002"),
            .. Enumerable.Range(0, 4).Select(_ => new Address(RandomBytes(random, Address.Size))),
        ];

        private readonly UInt256[] _slots = [0, 1, 2, 255, UInt256.MaxValue];
        private readonly TreePath[] _paths = CreatePaths(random);
        private readonly List<StateId> _canonical = [];
        private readonly List<StateId> _known = [];
        private readonly List<(TrieNode CSharp, TrieNode Rust, string What)> _nodes = [];
        private Hash256[] _owners = [];
        private bool _headCommittedLast;
        private int _reorgs;
        private string _step = "";

        private StateId Head => _canonical[^1];

        public void Run()
        {
            _owners = [.. _addresses.Take(3).Select(static address => address.ToAccountPath.ToCommitment())];
            _step = "genesis";
            AddCanonical(StateId.PreGenesis, RandomPlan());
            Compare();

            for (int step = 0; step < StepCount; step++)
            {
                int choice = random.Next(100);
                _step = $"step {step}";
                if (step % 50 == 25) AddCanonical(Head, LargePlan());
                else if (choice < 55) AddCanonical(Head, RandomPlan());
                else if (choice < 60) AddCanonical(Head, BlockPlan.Empty);
                else if (choice < 68) Reorg();
                else if (choice < 75) Sibling();
                else if (choice < 78) Duplicate();
                else if (choice < 87) AdvanceFinality();
                else if (choice < 94) Flush();
                else Drop();

                Persist();
                Compare();
            }

            _step = "final flush";
            Flush();
            Compare();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(rust.Resolve<Fork.IFlatDbManager>(), Is.InstanceOf<Fork.RustFlatDbManager>());
                Assert.That(csharp.Resolve<IFlatDbManager>(), Is.InstanceOf<FlatDbManager>());
                Assert.That(_reorgs, Is.GreaterThan(0), "the stream reorged deeper than the compact size");
                Assert.That(_nodes.Count(static node => node.CSharp.NodeType != NodeType.Unknown && node.CSharp.IsPersisted), Is.GreaterThan(0), "nodes were persisted");
                Assert.That(_nodes.Count(static node => node.CSharp.NodeType != NodeType.Unknown && !node.CSharp.IsPersisted), Is.GreaterThan(0), "nodes were left unpersisted");
            }
        }

        private void AddCanonical(StateId parent, BlockPlan plan)
        {
            StateId block = Child(parent);
            Add(parent, block, plan);
            ulong number = parent == StateId.PreGenesis ? 0 : parent.BlockNumber + 1;
            _canonical.RemoveRange((int)number, _canonical.Count - (int)number);
            _canonical.Add(block);
            _headCommittedLast = true;
        }

        private void Add(StateId parent, StateId block, BlockPlan plan)
        {
            _step += $" add {parent.BlockNumber}->{block}";
            TrieNode[] csharpNodes = csharp.Arm.Add(plan, parent, block, out byte[] csharpContent);
            TrieNode[] rustNodes = rust.Arm.Add(plan, parent, block, out byte[] rustContent);
            Assert.That(rustContent.ToHexString(), Is.EqualTo(csharpContent.ToHexString()), $"content of {block} after {_step}");
            for (int i = 0; i < csharpNodes.Length; i++) _nodes.Add((csharpNodes[i], rustNodes[i], $"node {i} of {block}"));
            _known.Add(block);
            _headCommittedLast = false;
        }

        /// <summary>A reorg deeper than the compact size, when the window above the persisted and finalized blocks has room.</summary>
        private void Reorg()
        {
            StateId persisted = Persisted();
            ulong floor = Math.Max(persisted == StateId.PreGenesis ? 0 : persisted.BlockNumber, finality.FinalizedBlockNumber);
            ulong depth = CompactSize + 1 + (ulong)random.Next((int)CompactSize);
            if (Head.BlockNumber < depth || Head.BlockNumber - depth < floor)
            {
                AddCanonical(Head, RandomPlan());
                return;
            }

            _step += $" reorg {depth}";
            _reorgs++;
            AddCanonical(_canonical[(int)(Head.BlockNumber - depth)], RandomPlan());
        }

        /// <summary>A block on a recent canonical state that stays off the canonical chain.</summary>
        private void Sibling()
        {
            int depth = random.Next(1, 4);
            if (_canonical.Count <= depth) return;
            StateId parent = _canonical[^(depth + 1)];
            Add(parent, Child(parent), RandomPlan());
        }

        /// <summary>A second snapshot ending at a state already added, which both refuse.</summary>
        private void Duplicate()
        {
            StateId block = Head;
            StateId parent = _canonical.Count > 1 ? _canonical[^2] : StateId.PreGenesis;
            _step += $" duplicate {block}";
            BlockPlan plan = RandomPlan();
            csharp.Arm.Add(plan, parent, block, out byte[] csharpContent);
            rust.Arm.Add(plan, parent, block, out byte[] rustContent);
            Assert.That(rustContent.ToHexString(), Is.EqualTo(csharpContent.ToHexString()), $"content of the duplicate {block}");
        }

        private void AdvanceFinality()
        {
            ulong lag = 2 * CompactSize + (ulong)random.Next(5);
            if (Head.BlockNumber < lag) return;
            ulong finalized = Head.BlockNumber - lag;
            if (finalized <= finality.FinalizedBlockNumber) return;

            _step += $" finalize {finalized}";
            finality.Finalize(finalized, block => _canonical[(int)block]);
        }

        private void Flush()
        {
            // FlushCache persists up to the last committed state, so commit the canonical head last.
            if (!_headCommittedLast) AddCanonical(Head, RandomPlan());
            _step += " flush";
            csharp.Arm.FlushCache(CancellationToken.None);
            rust.Arm.FlushCache(CancellationToken.None);
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                Assert.That(FlatTestDbs.Dump(rust.Db, column), Is.EqualTo(FlatTestDbs.Dump(csharp.Db, column)), $"{column} after {_step}");
            }
        }

        private void Drop()
        {
            _step += " drop";
            csharp.Arm.DropStateNotReachableFrom(Head);
            rust.Arm.DropStateNotReachableFrom(Head);
        }

        /// <summary>Persists what the head allows, until a call persists nothing more, as one call drains at most four snapshots.</summary>
        private void Persist()
        {
            csharp.PersistUpTo(Head);
            rust.PersistUpTo(Head);
        }

        private StateId Persisted()
        {
            StateId persisted = csharp.PersistenceManager.GetCurrentPersistedStateId();
            Assert.That(rust.PersistenceManager.GetCurrentPersistedStateId(), Is.EqualTo(persisted), $"persisted state after {_step}");
            return persisted;
        }

        private void Compare()
        {
            Persisted();

            StateId unknown = new(Head.BlockNumber, RandomHash());
            StateId known = _known[random.Next(_known.Count)];
            foreach (StateId state in new[] { Head, known, unknown })
            {
                Assert.That(rust.Arm.HasStateForBlock(state), Is.EqualTo(csharp.Arm.HasStateForBlock(state)), $"HasStateForBlock({state}) after {_step}");
            }

            // A pruned state may still be served from either bundle cache until the background persistence clears it, so
            // only retained states and one never added, which no cache holds, are read through the shared bundles.
            foreach (StateId state in csharp.Arm.HasStateForBlock(known) ? new[] { Head, known, unknown } : [Head, unknown])
            {
                List<string> expected = BundleReads.Read(csharp.Arm, state, _addresses, _slots, _paths, _owners);
                List<string> actual = BundleReads.Read(rust.Arm, state, _addresses, _slots, _paths, _owners);
                Assert.That(actual, Is.EqualTo(expected), $"reads at {state} after {_step}");
            }

            List<string> persistedDifferently = [.. _nodes.Where(static node => node.CSharp.IsPersisted != node.Rust.IsPersisted)
                .Select(static node => $"{node.What}: C# {node.CSharp.IsPersisted}, Rust {node.Rust.IsPersisted}")];
            Assert.That(persistedDifferently, Is.Empty, $"IsPersisted after {_step}");
        }

        private StateId Child(in StateId parent) => new(parent == StateId.PreGenesis ? 0 : parent.BlockNumber + 1, RandomHash());

        private BlockPlan RandomPlan() => new(
            [.. Enumerable.Range(0, random.Next(1, 5)).Select(_ => (RandomAddress(), random.Next(5) == 0 ? null : RandomAccount()))],
            [.. Enumerable.Range(0, random.Next(0, 8)).Select(_ => (RandomAddress(), _slots[random.Next(_slots.Length)], random.Next(4) == 0 ? (UInt256?)null : RandomValue()))],
            [.. Enumerable.Range(0, random.Next(6) == 0 ? 1 : 0).Select(_ => (RandomAddress(), random.Next(2) == 0))],
            [.. Enumerable.Range(0, random.Next(0, 6)).Select(_ => (_paths[random.Next(_paths.Length)], RandomHash().ToCommitment(), RandomRlpOrPlaceholder()))],
            [.. Enumerable.Range(0, random.Next(0, 6)).Select(_ => (_owners[random.Next(_owners.Length)], _paths[random.Next(_paths.Length)], RandomHash().ToCommitment(), RandomRlpOrPlaceholder()))]);

        /// <summary>A block large enough to be serialized on several threads, with keys of its own.</summary>
        private BlockPlan LargePlan() => new(
            [.. Enumerable.Range(0, 500).Select(_ => (new Address(RandomBytes(random, Address.Size)), (Account?)RandomAccount()))],
            [.. Enumerable.Range(0, 2000).Select(_ => (RandomAddress(), new UInt256(RandomBytes(random, 32), isBigEndian: true), (UInt256?)RandomValue()))],
            [],
            [.. Enumerable.Range(0, 1000).Select(_ => (RandomLongPath(), RandomHash().ToCommitment(), RandomRlpOrPlaceholder()))],
            [.. Enumerable.Range(0, 1500).Select(_ => (_owners[random.Next(_owners.Length)], RandomLongPath(), RandomHash().ToCommitment(), RandomRlpOrPlaceholder()))]);

        private TreePath RandomLongPath() =>
            TreePath.FromHexString(string.Concat(Enumerable.Range(0, 1 + random.Next(64)).Select(_ => "0123456789abcdef"[random.Next(16)])));

        private Address RandomAddress() => _addresses[random.Next(_addresses.Length)];

        private Account RandomAccount() => new(
            (ulong)random.Next(4),
            random.Next(3) == 0 ? UInt256.Zero : new UInt256((ulong)random.NextInt64()),
            random.Next(2) == 0 ? Keccak.EmptyTreeHash : new Hash256(RandomHash()),
            random.Next(2) == 0 ? Keccak.OfAnEmptyString : new Hash256(RandomHash()));

        private UInt256 RandomValue() => random.Next(3) == 0 ? UInt256.MaxValue : new UInt256((ulong)random.NextInt64(1, long.MaxValue));

        /// <summary>Node RLP shorter than a hash or at least as long, or none for a hash-only placeholder.</summary>
        private byte[]? RandomRlpOrPlaceholder() =>
            random.Next(6) == 0 ? null : RandomBytes(random, random.Next(2) == 0 ? 1 + random.Next(31) : 32 + random.Next(300));

        private ValueHash256 RandomHash() => new(RandomBytes(random, ValueHash256.MemorySize));

        /// <summary>Short paths, several of them prefixes of each other, and a few long ones.</summary>
        private static TreePath[] CreatePaths(Random random)
        {
            string nibbles = string.Concat(Enumerable.Range(0, 64).Select(_ => "0123456789abcdef"[random.Next(16)]));
            return [.. new[] { 0, 1, 2, 3, 5, 8, 17, 64 }.Select(length => TreePath.FromHexString(nibbles[..length])), TreePath.FromHexString("f")];
        }

        private static byte[] RandomBytes(Random random, int length)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            return bytes;
        }
    }
}
