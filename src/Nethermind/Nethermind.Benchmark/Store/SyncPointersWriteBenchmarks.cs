// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Sync pointer updates as the fast blocks feeds issue them while they wait for a batch: mostly the value already set.
/// </summary>
[MemoryDiagnoser]
public class SyncPointersWriteBenchmarks
{
    private const int Updates = 1024;
    private const ulong Pointer = 20_000_000;

    private TempPath _directory = null!;
    private IColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private IDb _metadataDb = null!;
    private SyncPointers _pointers = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            RocksDbFactory factory = new(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path);
            _receiptsDb = factory.CreateColumnsDb<ReceiptsColumns>(new DbSettings(DbNames.Receipts, DbNames.Receipts));
            _metadataDb = factory.CreateDb(new DbSettings(DbNames.Metadata, DbNames.Metadata));
        }

        _pointers = new SyncPointers(new TestMemDb(), _receiptsDb, _metadataDb, new ReceiptConfig())
        {
            LowestInsertedReceiptBlockNumber = Pointer,
            LowestInsertedBodyNumber = Pointer
        };
    }

    [Benchmark]
    public ulong? SetUnchangedReceiptPointer()
    {
        for (int i = 0; i < Updates; i++)
        {
            _pointers.LowestInsertedReceiptBlockNumber = Pointer;
        }

        return _pointers.LowestInsertedReceiptBlockNumber;
    }

    [Benchmark]
    public ulong? SetUnchangedBodyPointer()
    {
        for (int i = 0; i < Updates; i++)
        {
            _pointers.LowestInsertedBodyNumber = Pointer;
        }

        return _pointers.LowestInsertedBodyNumber;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _metadataDb.Dispose();
        _receiptsDb.Dispose();
        _directory.Dispose();
    }
}
