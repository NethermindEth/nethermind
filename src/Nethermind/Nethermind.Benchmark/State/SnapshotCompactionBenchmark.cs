// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.Trie;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Benchmarks <see cref="SnapshotCompactor.CompactSnapshotBundle"/> — merging a window of per-block
/// <see cref="FlatSnapshot"/>s into a single compacted snapshot. Measures the merge in isolation: the
/// input snapshots are built once in <see cref="Setup"/> and only read during the benchmark.
/// </summary>
/// <remarks>
/// The per-snapshot write profile is calibrated to a representative mainnet block from the flat state-diff
/// archive (~2k account writes and ~10k storage slots across ~200 contracts), with the trie-node count set
/// to ~5x the flat account/slot write count (node bytes end up ~20x the flat write bytes). A fixed fraction of each snapshot's
/// keys is drawn from a shared hot set reused across all snapshots, so the merge exercises the
/// overwrite/dedup path rather than a pure disjoint union. <see cref="SelfDestructsPerSnapshot"/> toggles
/// the per-self-destruct full-storage scan, the one super-linear path in the merge.
/// </remarks>
[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class SnapshotCompactionBenchmark
{
    private const int AccountsPerSnapshot = 2000;
    private const int StorageContracts = 200;
    private const int SlotsPerContract = 50;            // 10,000 slots per snapshot
    // Trie-node count is ~5x the flat account/slot write count: ~5x accounts of state nodes and
    // ~5x slots of storage nodes, for 60,000 nodes over 12,000 flat writes per snapshot.
    private const int StateNodesPerSnapshot = 10_000;
    private const int StorageNodesPerContract = 250;    // 50,000 storage nodes per snapshot
    private const double HotFraction = 0.25;            // shared, reused-across-snapshots key fraction

    // Disjoint id ranges per key category so derived addresses/paths never collide across categories.
    private const long AccountBase = 1;
    private const long ContractBase = 100_000_000;
    private const long StateNodeBase = 200_000_000;
    private const long StorageNodeBase = 300_000_000;

    [Params(2, 8, 32)]
    public int SnapshotCount;

    // 0 measures the pure merge; > 0 additionally exercises the full accumulated-storage scan per self-destruct.
    [Params(0, 16)]
    public int SelfDestructsPerSnapshot;

    private static readonly TrieNode PlaceholderNode = new(NodeType.Leaf, Keccak.EmptyTreeHash);

    private SnapshotCompactor _compactor = null!;
    private SnapshotPooledList _snapshots = null!;

    [GlobalSetup]
    public void Setup()
    {
        FlatDbConfig config = new() { CompactSize = 2048, CompactionOffset = 0 };
        ResourcePool resourcePool = new(config);
        CompactionSchedule schedule = new(new MemDb(), config, LimboLogs.Instance);
        SnapshotRepository repository = new(LimboLogs.Instance);
        _compactor = new SnapshotCompactor(config, schedule, resourcePool, repository, LimboLogs.Instance);

        // Contract addresses and their account-path hashes are shared across every snapshot (the same hot
        // contracts are touched each block), so only the slots/nodes underneath them differ per snapshot.
        Address[] contracts = new Address[StorageContracts];
        Hash256[] contractHashes = new Hash256[StorageContracts];
        for (int c = 0; c < StorageContracts; c++)
        {
            contracts[c] = DeriveAddress(ContractBase + c);
            contractHashes[c] = Keccak.Compute(contracts[c].Bytes);
        }

        _snapshots = new SnapshotPooledList(SnapshotCount);
        for (int b = 0; b < SnapshotCount; b++)
        {
            FlatSnapshot snapshot = resourcePool.CreateSnapshot(
                CreateStateId((ulong)b), CreateStateId((ulong)(b + 1)), ResourcePool.Usage.ReadOnlyProcessingEnv);
            Fill(snapshot, b, contracts, contractHashes);
            _snapshots.Add(snapshot);
        }
    }

    [Benchmark]
    public FlatSnapshot Compact() => _compactor.CompactSnapshotBundle(_snapshots);

    private void Fill(FlatSnapshot snapshot, int b, Address[] contracts, Hash256[] contractHashes)
    {
        SnapshotContent content = snapshot.Content;
        Span<byte> slotBuffer = stackalloc byte[3];

        int hotAccounts = (int)(AccountsPerSnapshot * HotFraction);
        for (int i = 0; i < AccountsPerSnapshot; i++)
        {
            long id = AccountBase + SeqOffset(i, hotAccounts, AccountsPerSnapshot, b);
            content.Accounts[DeriveAddress(id)] = new Account((UInt256)(ulong)(b + 1));
        }

        int hotStateNodes = (int)(StateNodesPerSnapshot * HotFraction);
        for (int i = 0; i < StateNodesPerSnapshot; i++)
        {
            long id = StateNodeBase + SeqOffset(i, hotStateNodes, StateNodesPerSnapshot, b);
            content.StateNodes[DerivePath(id)] = PlaceholderNode;
        }

        int hotSlots = (int)(SlotsPerContract * HotFraction);
        int hotStorageNodes = (int)(StorageNodesPerContract * HotFraction);
        for (int c = 0; c < StorageContracts; c++)
        {
            Address contract = contracts[c];
            Hash256 contractHash = contractHashes[c];

            for (int s = 0; s < SlotsPerContract; s++)
            {
                UInt256 slot = (UInt256)(ulong)SeqOffset(s, hotSlots, SlotsPerContract, b);
                slotBuffer[0] = (byte)b;
                slotBuffer[1] = (byte)s;
                slotBuffer[2] = (byte)c;
                content.Storages[(contract, slot)] = new SlotValue(slotBuffer);
            }

            for (int n = 0; n < StorageNodesPerContract; n++)
            {
                long id = StorageNodeBase + c * (long)StorageNodesPerContract + SeqOffset(n, hotStorageNodes, StorageNodesPerContract, b);
                content.StorageNodes[(contractHash, DerivePath(id))] = PlaceholderNode;
            }
        }

        // Self-destructs from block b clear the storage those contracts accumulated in earlier snapshots.
        // isNewAccount == false is the "processed" (delete) kind that drives the full accumulated-storage scan.
        for (int i = 0; i < SelfDestructsPerSnapshot && i < StorageContracts; i++)
        {
            content.SelfDestructedStorageAddresses[contracts[i]] = false;
        }
    }

    /// <summary>
    /// Maps a per-snapshot key index to a global offset: the first <paramref name="hot"/> indices map to a
    /// shared range reused by every snapshot; the rest map to a per-snapshot unique range.
    /// </summary>
    private static long SeqOffset(int i, int hot, int count, int b)
        => i < hot ? i : (long)hot + (long)b * (count - hot) + (i - hot);

    private static Address DeriveAddress(long index) =>
        new(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));

    private static TreePath DerivePath(long index) =>
        TreePath.FromPath(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes).Bytes);

    private static StateId CreateStateId(ulong blockNumber)
    {
        byte[] root = new byte[32];
        BitConverter.TryWriteBytes(root, blockNumber);
        return new StateId(blockNumber, new ValueHash256(root));
    }
}
