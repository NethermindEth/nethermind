// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>Reads 8-byte block numbers by block hash from a real RocksDB, as the header store does.</summary>
[MemoryDiagnoser]
public class EightByteValueReadBenchmark
{
    private const int Keys = 4096;
    private TempPath _directory = null!;
    private DbOnTheRocks _db = null!;
    private Hash256[] _hashes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            _db = (DbOnTheRocks)new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), configs.Resolve<IDbConfig>(),
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateDb(new DbSettings(DbNames.BlockNumbers, DbNames.BlockNumbers));
        }

        _hashes = new Hash256[Keys];
        Span<byte> number = stackalloc byte[8];
        for (int i = 0; i < Keys; i++)
        {
            _hashes[i] = Keccak.Compute(BitConverter.GetBytes(i));
            BinaryPrimitives.WriteUInt64BigEndian(number, 25_000_000UL + (ulong)i);
            _db.PutSpan(_hashes[i].Bytes, number, WriteFlags.None);
        }
        _db.Flush();
        if (GetSpanAndRelease() != GetIntoStackBuffer()) throw new InvalidOperationException("Read paths disagree.");
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Keys)]
    public ulong GetSpanAndRelease()
    {
        IReadOnlyKeyValueStore db = _db;
        ulong sum = 0;
        foreach (Hash256 hash in _hashes)
        {
            Span<byte> span = db.GetSpan(hash.Bytes);
            sum += BinaryPrimitives.ReadUInt64BigEndian(span);
            db.DangerousReleaseMemory(span);
        }
        return sum;
    }

    [Benchmark(OperationsPerInvoke = Keys)]
    public ulong GetIntoStackBuffer()
    {
        IReadOnlyKeyValueStore db = _db;
        ulong sum = 0;
        Span<byte> buffer = stackalloc byte[8];
        foreach (Hash256 hash in _hashes)
        {
            db.Get(hash.Bytes, buffer);
            sum += BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }
        return sum;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _directory.Dispose();
    }
}
