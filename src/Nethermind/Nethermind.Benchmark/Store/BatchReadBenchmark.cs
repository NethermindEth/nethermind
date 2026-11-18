// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Testably.Abstractions;

namespace Nethermind.Benchmarks.Store;

/// <summary>Reads 256 random keys from RocksDB in <see cref="IBatchReadOnlyDb.GetMany"/> batches.</summary>
/// <remarks>
/// The values are in the code database, whose block cache is small. <see cref="StorageMode.Disk"/> uses direct
/// reads, so each call reads its keys from the disk. <see cref="StorageMode.PageCache"/> has every file page in the
/// OS page cache, and <see cref="StorageMode.BlockCache"/> has every block in a large block cache.
/// </remarks>
[MemoryDiagnoser]
public class BatchReadBenchmark
{
    private const int KeyLength = 32;
    private const int KeysPerCall = 256;
    private const long StoredBytes = 256L << 20;

    public enum StorageMode { Disk, PageCache, BlockCache }

    private TempPath _directory = null!;
    private DbOnTheRocks _db = null!;
    private byte[][] _calls = null!;
    private byte[] _sink = null!;
    private int _nextCall;

    [Params(512, 8_192)]
    public int ValueSize { get; set; }

    [Params(4, 32)]
    public int BatchSize { get; set; }

    [Params(StorageMode.Disk, StorageMode.PageCache, StorageMode.BlockCache)]
    public StorageMode Storage { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        using (IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
        {
            IDbConfig dbConfig = configs.Resolve<IDbConfig>();
            dbConfig.AdditionalRocksDbOptions = Storage switch
            {
                StorageMode.Disk => "use_direct_reads=true;",
                StorageMode.BlockCache => "block_based_table_factory.block_cache=1073741824;",
                _ => null
            };
            _db = (DbOnTheRocks)new RocksDbFactory(configs.Resolve<IRocksDbConfigFactory>(), dbConfig,
                    new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _directory.Path)
                .CreateDb(new DbSettings(DbNames.Code, DbNames.Code));
        }

        int stored = (int)(StoredBytes / ValueSize);
        Random random = new(42);
        byte[][] keys = new byte[stored][];
        byte[] value = new byte[ValueSize];
        IWriteBatch batch = _db.StartWriteBatch();
        for (int i = 0; i < stored; i++)
        {
            keys[i] = new byte[KeyLength];
            random.NextBytes(keys[i]);
            random.NextBytes(value);
            batch.Set(keys[i], value);
            if (i % 1024 == 1023)
            {
                batch.Dispose();
                batch = _db.StartWriteBatch();
            }
        }
        batch.Dispose();
        _db.Flush();
        _db.Compact();

        random.Shuffle(keys);
        _calls = new byte[stored / KeysPerCall][];
        for (int c = 0; c < _calls.Length; c++)
        {
            _calls[c] = new byte[KeysPerCall * KeyLength];
            for (int k = 0; k < KeysPerCall; k++) keys[c * KeysPerCall + k].CopyTo(_calls[c], k * KeyLength);
        }

        _sink = new byte[ValueSize];
        if (Storage != StorageMode.Disk)
        {
            foreach (byte[] call in _calls) Read(call);
        }
    }

    [Benchmark]
    public int GetMany() => Read(_calls[_nextCall++ % _calls.Length]);

    private int Read(ReadOnlySpan<byte> keys)
    {
        int batchBytes = BatchSize * KeyLength;
        Span<bool> found = stackalloc bool[BatchSize];
        int count = 0;
        for (int offset = 0; offset < keys.Length; offset += batchBytes)
        {
            ((IBatchReadOnlyDb)_db).GetMany(keys.Slice(offset, batchBytes), KeyLength, found, new CopyDecoder(_sink));
            foreach (bool f in found) if (f) count++;
        }
        return count;
    }

    private readonly struct CopyDecoder(byte[] sink) : IValueDecoder<bool>
    {
        public bool Decode(ReadOnlySpan<byte> buffer)
        {
            buffer.CopyTo(sink);
            return true;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        _directory.Dispose();
    }
}
