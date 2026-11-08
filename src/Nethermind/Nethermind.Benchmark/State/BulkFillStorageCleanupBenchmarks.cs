// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Changesets;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Bulk transaction index replay on a RocksDB scratch database: each block clears the storage of a number of new
/// contracts, commits, and runs the storage cleanup, after a given number of blocks were already replayed.
/// </summary>
[MemoryDiagnoser]
public class BulkFillStorageCleanupBenchmarks
{
    private const ulong ClearsPerBlock = 50;
    private const int BlocksPerOperation = 100;
    private static readonly Account Contract = Build.An.Account.WithBalance(1).TestObject;

    private string _path = null!;
    private BulkFillSession _session = null!;
    private BlockHeader _head = null!;
    private ulong _contracts;

    [Params(0, 5_000, 20_000)]
    public int ReplayedBlocks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), "nethermind-bulkfill-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        DbConfig dbConfig = new();
        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(8L * 1024 * 1024 * 1024), NullLogManager.Instance, validateConfig: false);
        RocksDbFactory factory = new(configFactory, dbConfig, new HyperClockCacheWrapper(), new RealFileSystem(), NullLogManager.Instance, _path);

        _head = Build.A.BlockHeader.WithNumber(0).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        _session = new BulkFillSession(factory, new MemDb(), Keccak.Compute(_path), _head, rlpWrappedSlots: false);
        _session.ImportGenesis([], CancellationToken.None);
        for (int i = 0; i < ReplayedBlocks; i++) ReplayBlock();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _session.Dispose();
        Directory.Delete(_path, recursive: true);
    }

    [Benchmark(OperationsPerInvoke = BlocksPerOperation)]
    public void ReplayBlocks()
    {
        for (int i = 0; i < BlocksPerOperation; i++) ReplayBlock();
    }

    private void ReplayBlock()
    {
        BlockHeader block = Build.A.BlockHeader.WithParent(_head).WithStateRoot(Keccak.EmptyTreeHash).TestObject;
        _session.BeginBlock(block);
        _session.StageFinalState(() => new NewContractsSnapshot(_contracts, ClearsPerBlock));
        _session.CommitBlock();
        _session.CleanStorage(CancellationToken.None);
        _contracts += ClearsPerBlock;
        _head = block;
    }

    /// <summary>A block that creates contracts: each gets a storage clear followed by one slot write.</summary>
    private sealed class NewContractsSnapshot(ulong first, ulong count) : IWorldStateScopeProvider.IBlockChangeSnapshot
    {
        public void WriteTo(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
        {
            Span<byte> seed = stackalloc byte[sizeof(ulong)];
            for (ulong i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(seed, first + i);
                Address contract = new(Keccak.Compute(seed).Bytes[..Address.Size]);
                writeBatch.Set(contract, Contract);
                using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(contract, 1);
                storage.Clear();
                storage.Set(UInt256.One, UInt256.One);
            }
        }

        public void Dispose() { }
    }
}
