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
using Nethermind.Logging;
using Nethermind.State.SnapServer;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>Serves a snap GetByteCodes request of random code hashes from a RocksDB code database.</summary>
/// <remarks>Each call asks for other codes, and all of them are several times the code block cache.</remarks>
[MemoryDiagnoser]
public class SnapByteCodesServingBenchmark
{
    private const int StoredCodes = 40_000;
    private const int RequestedCodes = 1024;

    private TempPath _directory = null!;
    private DbOnTheRocks _db = null!;
    private IContainer _container = null!;
    private ISnapServer _server = null!;
    private ValueHash256[][] _requests = null!;
    private int _nextRequest;

    [Params(500, 3_000)]
    public int AverageCodeSize { get; set; }

    [Params(4_096, ISnapStateServer.HardResponseByteLimit)]
    public long ByteLimit { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            _db = (DbOnTheRocks)new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateDb(new DbSettings(DbNames.Code, DbNames.Code));
        }

        Random random = new(42);
        ValueHash256[] hashes = new ValueHash256[StoredCodes];
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int i = 0; i < StoredCodes; i++)
            {
                byte[] code = new byte[random.Next(AverageCodeSize / 2, AverageCodeSize * 3 / 2)];
                random.NextBytes(code);
                hashes[i] = ValueKeccak.Compute(code);
                batch.Set(hashes[i].Bytes, code);
            }
        }
        _db.Flush();

        random.Shuffle(hashes);
        _requests = new ValueHash256[StoredCodes / RequestedCodes][];
        for (int i = 0; i < _requests.Length; i++)
        {
            _requests[i] = hashes[(i * RequestedCodes)..((i + 1) * RequestedCodes)];
        }

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddKeyedSingleton<IDb>(DbNames.Code, _db)
            .Build();
        _server = _container.Resolve<ISnapServer>();

        using IByteArrayList codes = _server.GetByteCodes(_requests[0], ByteLimit, CancellationToken.None);
        if (codes.Count == 0 || ValueKeccak.Compute(codes[0]) != _requests[0][0])
            throw new InvalidOperationException("Wrong code was served.");
    }

    [Benchmark]
    public int ServeByteCodes()
    {
        ValueHash256[] request = _requests[_nextRequest++ % _requests.Length];
        using IByteArrayList codes = _server.GetByteCodes(request, ByteLimit, CancellationToken.None);
        return codes.Count;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _container.Dispose();
        _db.Dispose();
        _directory.Dispose();
    }
}
