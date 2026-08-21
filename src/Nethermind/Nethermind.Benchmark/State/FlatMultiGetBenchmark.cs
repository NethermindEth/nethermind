// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat;
using IWriteBatch = Nethermind.Core.IWriteBatch;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Measures the flat base-read tier at the RocksDB boundary: a loop of point reads versus one batched
/// <c>MultiGet</c> over the same keys.
/// </summary>
/// <remarks>
/// The working set is small enough to stay block-cache resident, which is the regime the batching targets —
/// the per-key cost being amortized is the interop transition plus the lookup setup, not disk I/O. Keys use
/// the flat Storage shape (52-byte split key, ≤33-byte value) so the comparison reflects production sizes.
/// </remarks>
[MemoryDiagnoser]
[WarmupCount(3)]
[MinIterationCount(3)]
[MaxIterationCount(10)]
public class FlatMultiGetBenchmark
{
    private const int KeyLength = 52;
    private const int ValueStride = 33;
    private const int KeyCount = 200_000;

    [Params(16, 32, 64, 128)]
    public int BatchSize;

    private string _dbPath = null!;
    private ColumnsDb<FlatDbColumns> _db = null!;
    private IBatchedReadOnlyKeyValueStore _batched = null!;
    private IReadOnlyKeyValueStore _perKey = null!;

    // Key blobs are built up front so the measured region contains only the reads, not the Keccak that
    // derives the keys. Several of them, rotated per invocation, so the measurement is not one batch served
    // from the hottest possible cache state.
    private const int BatchVariants = 64;

    private byte[][] _keyBatches = null!;
    private byte[] _values = null!;
    private int[] _lengths = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "flat-multiget-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbPath);

        _db = new ColumnsDb<FlatDbColumns>(_dbPath,
            new DbSettings("Flat", _dbPath) { DeleteOnStart = true },
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<FlatDbColumns>());

        IDb storage = _db.GetColumnDb(FlatDbColumns.Storage);
        using (IWriteBatch batch = storage.StartWriteBatch())
        {
            Span<byte> key = stackalloc byte[KeyLength];
            Span<byte> value = stackalloc byte[ValueStride];
            for (int i = 0; i < KeyCount; i++)
            {
                WriteKey(key, i);
                value.Fill((byte)i);
                batch.PutSpan(key, value);
            }
        }

        // Materialize into SSTs so reads exercise the table/block-cache path rather than the memtable.
        _db.Flush();

        _batched = (IBatchedReadOnlyKeyValueStore)storage;
        _perKey = storage;

        _values = new byte[BatchSize * ValueStride];
        _lengths = new int[BatchSize];
        _keyBatches = new byte[BatchVariants][];
        for (int b = 0; b < BatchVariants; b++)
        {
            byte[] batch = new byte[BatchSize * KeyLength];
            for (int i = 0; i < BatchSize; i++)
            {
                // One key in three is absent, matching the hit/miss mix a BAL prefetch sees; the rest are
                // scattered across the keyspace rather than adjacent.
                int slot = b * BatchSize + i;
                int index = (i % 3 == 2) ? KeyCount + slot : (slot * 7919) % KeyCount;
                WriteKey(batch.AsSpan(i * KeyLength, KeyLength), index);
            }

            _keyBatches[b] = batch;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        try { Directory.Delete(_dbPath, recursive: true); }
        catch (IOException) { /* best effort: a leftover bench DB is harmless */ }
    }

    /// <summary>Writes the 52-byte flat storage key for a synthetic slot index, spread across the keyspace.</summary>
    private static void WriteKey(Span<byte> destination, int index)
    {
        ValueHash256 hash = ValueKeccak.Compute(BitConverter.GetBytes(index));
        hash.Bytes[..4].CopyTo(destination);
        hash.Bytes.CopyTo(destination[4..36]);
        hash.Bytes[4..20].CopyTo(destination[36..52]);
    }

    private byte[] NextBatch()
    {
        byte[] batch = _keyBatches[_cursor];
        _cursor = (_cursor + 1) % BatchVariants;
        return batch;
    }

    [Benchmark(Baseline = true)]
    public int PerKey()
    {
        Span<byte> keys = NextBatch();
        Span<byte> values = _values;
        int total = 0;
        for (int i = 0; i < BatchSize; i++)
        {
            total += _perKey.Get(keys.Slice(i * KeyLength, KeyLength), values.Slice(i * ValueStride, ValueStride));
        }

        return total;
    }

    [Benchmark]
    public int Batched()
    {
        _batched.MultiGet(NextBatch(), KeyLength, _values, ValueStride, _lengths);

        int total = 0;
        for (int i = 0; i < BatchSize; i++) total += _lengths[i];
        return total;
    }
}
