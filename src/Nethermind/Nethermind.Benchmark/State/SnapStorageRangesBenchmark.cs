// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State;

/// <summary>Serves one snap storage-ranges request that covers many small contracts from their first slot.</summary>
[MemoryDiagnoser]
public class SnapStorageRangesBenchmark
{
    public enum ServerKind
    {
        Patricia,
        Flat
    }

    private const int AccountCount = 512;
    private const int SlotsPerAccount = 4;

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb;
    private INodeRecordBuffer _nodeRecordBuffer;
    private ISnapStateServer _server = null!;
    private Hash256 _rootHash = null!;
    private PathWithAccount[] _accounts = null!;

    [Params(ServerKind.Patricia, ServerKind.Flat)]
    public ServerKind Server { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        NodeStorage nodeStorage = new(new MemDb());
        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), NullLogManager.Instance);
        Hash256[] paths = new Hash256[AccountCount];
        for (int i = 0; i < AccountCount; i++) paths[i] = Keccak.Compute(BitConverter.GetBytes(i));
        Array.Sort(paths);

        _accounts = new PathWithAccount[AccountCount];
        for (int i = 0; i < AccountCount; i++)
        {
            StorageTree storageTree = new(new RawScopedTrieStore(nodeStorage, paths[i]), NullLogManager.Instance);
            for (int slot = 1; slot <= SlotsPerAccount; slot++) storageTree.Set((UInt256)slot, [(byte)slot]);
            storageTree.Commit();

            Account account = new Account((UInt256)i).WithChangedStorageRoot(storageTree.RootHash);
            stateTree.Set(paths[i], account);
            _accounts[i] = new PathWithAccount(paths[i], account);
        }

        stateTree.Commit();
        _rootHash = stateTree.RootHash;

        if (Server == ServerKind.Patricia)
        {
            _server = new SnapStateServer(new TestRawTrieStore(nodeStorage).AsReadOnly(), NullLogManager.Instance);
            return;
        }

        StateId stateId = new(0, _rootHash.ValueHash256);
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        RocksDbPersistence persistence = new(_columnsDb, NullLogManager.Instance, NullTrieNodeLog.Instance);
        new Importer(nodeStorage, persistence, NullLogManager.Instance).Copy(stateId).GetAwaiter().GetResult();
        _nodeRecordBuffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        _server = new SnapFlatStateServer(
            new SnapFlatStateServerBenchmark.InMemoryFlatDbManager(persistence, _nodeRecordBuffer, []),
            new SnapFlatStateServerBenchmark.SingleStateRootIndex(stateId),
            NullLogManager.Instance);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nodeRecordBuffer?.Dispose();
        _columnsDb?.Dispose();
    }

    [Benchmark]
    public int StorageRangesFromStart()
    {
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList proofs) =
            _server.GetStorageRanges(_rootHash, _accounts, null, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        int count = slots.Count;
        slots.DisposeRecursive();
        proofs?.Dispose();
        return count;
    }
}
