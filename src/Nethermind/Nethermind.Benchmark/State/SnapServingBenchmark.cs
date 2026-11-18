// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
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
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.State;

/// <summary>Serves each snap/1 request type filled to the response byte limit from a flat state persisted in RocksDB.</summary>
[MemoryDiagnoser]
public class SnapServingBenchmark
{
    private const int EoaCount = 100_000;
    private const int ContractCount = 2_000;
    private const int SlotsPerContract = 64;
    private const int CodeCount = 512;
    private const int MinCodeLength = 2_048;
    private const int MaxCodeLength = 16_384;
    private const int StateTrieNodeDepth = 3;
    private const int StorageTrieNodesPerContract = 16;

    private TempPath _directory = null!;
    private IColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IDb _codeDb = null!;
    private INodeRecordBuffer _nodeRecordBuffer = null!;
    private SnapServer _server = null!;
    private Hash256 _rootHash = null!;
    private PathWithAccount[] _contracts = null!;
    private ValueHash256[] _codeHashes = null!;
    private PathGroup[] _stateTrieNodePaths = null!;
    private PathGroup[] _storageTrieNodePaths = null!;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(42);
        NodeStorage nodeStorage = new(new MemDb());
        StateTree stateTree = new(new RawScopedTrieStore(nodeStorage), NullLogManager.Instance);
        for (int i = 0; i < EoaCount; i++) stateTree.Set(Keccak.Compute(BitConverter.GetBytes(i)), new Account((UInt256)i));

        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            RocksDbFactory factory = new(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path);
            _columnsDb = factory.CreateColumnsDb<FlatDbColumns>(new DbSettings(DbNames.Flat, DbNames.Flat));
            _codeDb = factory.CreateDb(new DbSettings(DbNames.Code, DbNames.Code));
        }

        _codeHashes = new ValueHash256[CodeCount];
        for (int i = 0; i < CodeCount; i++)
        {
            byte[] code = new byte[random.Next(MinCodeLength, MaxCodeLength)];
            random.NextBytes(code);
            _codeHashes[i] = ValueKeccak.Compute(code);
            _codeDb.Set(_codeHashes[i], code);
        }

        Hash256[] contractPaths = new Hash256[ContractCount];
        for (int i = 0; i < ContractCount; i++) contractPaths[i] = Keccak.Compute(BitConverter.GetBytes(EoaCount + i));
        Array.Sort(contractPaths);

        _contracts = new PathWithAccount[ContractCount];
        for (int i = 0; i < ContractCount; i++)
        {
            StorageTree storageTree = new(new RawScopedTrieStore(nodeStorage, contractPaths[i]), NullLogManager.Instance);
            for (int slot = 1; slot <= SlotsPerContract; slot++) storageTree.Set((UInt256)slot, ((UInt256)(ulong)random.NextInt64()).ToBigEndian());
            storageTree.Commit();

            Account account = new(1, (UInt256)i, storageTree.RootHash, _codeHashes[i % CodeCount].ToCommitment());
            stateTree.Set(contractPaths[i], account);
            _contracts[i] = new PathWithAccount(contractPaths[i], account.ToStruct());
        }

        stateTree.Commit();
        _rootHash = stateTree.RootHash;
        _codeDb.Flush();

        StateId stateId = new(0, _rootHash.ValueHash256);
        RocksDbPersistence persistence = new(_columnsDb, NullLogManager.Instance, NullTrieNodeLog.Instance);
        new Importer(nodeStorage, persistence, NullLogManager.Instance).Copy(stateId).GetAwaiter().GetResult();
        _columnsDb.Flush();
        _nodeRecordBuffer = new NodeRecordBuffer(0, NodeRecordBuffer.DefaultGenerationBytes);
        SnapFlatStateServer stateServer = new(
            new SnapFlatStateServerBenchmark.InMemoryFlatDbManager(persistence, _nodeRecordBuffer, []),
            new SnapFlatStateServerBenchmark.SingleStateRootIndex(stateId),
            NullLogManager.Instance);
        _server = new SnapServer(stateServer, _codeDb, null!, null!);

        _stateTrieNodePaths = BuildStateTrieNodePaths();
        _storageTrieNodePaths = BuildStorageTrieNodePaths();
    }

    private static PathGroup[] BuildStateTrieNodePaths()
    {
        int count = 1 << (4 * StateTrieNodeDepth);
        PathGroup[] groups = new PathGroup[count];
        Span<byte> nibbles = stackalloc byte[StateTrieNodeDepth];
        for (int i = 0; i < count; i++)
        {
            for (int n = 0; n < StateTrieNodeDepth; n++) nibbles[n] = (byte)((i >> (4 * (StateTrieNodeDepth - 1 - n))) & 0xF);
            groups[i] = new PathGroup { Group = [Nibbles.ToCompactHexEncoding(nibbles)] };
        }

        return groups;
    }

    private PathGroup[] BuildStorageTrieNodePaths()
    {
        PathGroup[] groups = new PathGroup[ContractCount];
        for (int i = 0; i < ContractCount; i++)
        {
            byte[][] group = new byte[StorageTrieNodesPerContract + 1][];
            group[0] = _contracts[i].Path.BytesAsSpan.ToArray();
            for (int n = 0; n < StorageTrieNodesPerContract; n++) group[n + 1] = Nibbles.ToCompactHexEncoding([(byte)n]);
            groups[i] = new PathGroup { Group = group };
        }

        return groups;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nodeRecordBuffer.Dispose();
        _columnsDb.Dispose();
        _codeDb.Dispose();
        _directory.Dispose();
    }

    [Benchmark]
    public long AccountRange()
    {
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            _server.GetAccountRanges(_rootHash, ValueKeccak.Zero, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        using (accounts)
        using (proofs)
        {
            return accounts.Count;
        }
    }

    [Benchmark]
    public long StorageRanges()
    {
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList proofs) =
            _server.GetStorageRanges(_rootHash, _contracts, null, null, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        int count = slots.Count;
        slots.Dispose();
        proofs?.Dispose();
        return count;
    }

    [Benchmark]
    public long ByteCodes()
    {
        using IByteArrayList codes = _server.GetByteCodes(_codeHashes, ISnapStateServer.HardResponseByteLimit, CancellationToken.None);
        return codes.Count;
    }

    [Benchmark]
    public long StateTrieNodes() => ServeTrieNodes(_stateTrieNodePaths);

    [Benchmark]
    public long StorageTrieNodes() => ServeTrieNodes(_storageTrieNodePaths);

    private long ServeTrieNodes(PathGroup[] paths)
    {
        using IByteArrayList nodes = _server.GetTrieNodes(paths, _rootHash, ISnapStateServer.HardResponseByteLimit, CancellationToken.None)!;
        long bytes = 0;
        for (int i = 0; i < nodes.Count; i++) bytes += nodes[i].Length;
        return bytes;
    }
}
