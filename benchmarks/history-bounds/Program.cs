using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat.History;

ManualConfig config = ManualConfig.Create(DefaultConfig.Instance);
config.BuildTimeout = TimeSpan.FromMinutes(10);
BenchmarkSwitcher.FromAssembly(typeof(HistoryBoundsBenchmarks).Assembly).Run(args, config);

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class HistoryBoundsBenchmarks
{
    private const int Groups = 25000;
    private const int Versions = 8;
    private const int Queries = 8192;
    private DbOnTheRocks _db = null!;
    private HistoryStoreV3 _store = null!;
    private byte[][] _keys = null!;
    private int[] _groups = null!;
    private string _root = null!;

    [Params("active", "deleted95", "compacted95")]
    public string State { get; set; } = "active";

    [Params(1, 24, 256)]
    public int ScopeSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("BENCH_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        DbConfig config = new() { BlocksDbAdditionalRocksDbOptions = "disable_auto_compactions=true;" };
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new BenchmarkHardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new DbOnTheRocks(_root, new DbSettings("Blocks", "history"), config, factory, LimboLogs.Instance);
        _store = new HistoryStoreV3(_db);
        using (IWriteBatch batch = _db.StartWriteBatch())
        {
            for (int group = 0; group < Groups; group++)
                for (ulong version = 0; version < Versions; version++)
                    _store.RecordPreValue(version, Key(group), Value(group, version), batch);
        }
        _db.Flush();
        if (State != "active")
        {
            using (IWriteBatch batch = _db.StartWriteBatch())
            {
                for (int group = 0; group < Groups; group++)
                    if (group % 20 != 0)
                        for (ulong version = 0; version < Versions; version++)
                        {
                            byte[] key = new byte[40];
                            Key(group).CopyTo(key, 0);
                            BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(32), version);
                            batch.Set(key, null);
                        }
            }
            _db.Flush();
        }
        if (State == "compacted95") _db.Compact();
        _keys = new byte[Queries][];
        _groups = new int[Queries];
        Random random = new(1701);
        for (int i = 0; i < Queries; i++)
        {
            _groups[i] = random.Next(Groups);
            _keys[i] = Key(_groups[i]);
        }
        Verify();
    }

    private void Verify()
    {
        Span<byte> output = stackalloc byte[64];
        for (int start = 0; start < Queries; start += ScopeSize)
        {
#if REUSE_BOUNDS
            using HistoryReader.ReadScope scope = new();
#endif
            int end = Math.Min(start + ScopeSize, Queries);
            for (int i = start; i < end; i++)
            {
#if REUSE_BOUNDS
                int written = _store.TryGetValueBeforeNextChange(3, _keys[i], output, out ulong next, scope.Accounts, generation: 0);
#else
                int written = _store.TryGetValueBeforeNextChange(3, _keys[i], output, out ulong next);
#endif
                bool present = State == "active" || _groups[i] % 20 == 0;
                if (written != (present ? 64 : -1) || (present && (next != 4 || !output.SequenceEqual(Value(_groups[i], 4)))))
                    throw new InvalidOperationException($"Incorrect history lookup {i}, state={State}, scope={ScopeSize}");
            }
        }
        Console.WriteLine($"Verified {Queries} exact HistoryStoreV3 results; state={State}; scope={ScopeSize}");
    }

    [Benchmark(OperationsPerInvoke = Queries)]
    public long ReadHistory()
    {
        long checksum = 0;
        Span<byte> output = stackalloc byte[64];
        for (int start = 0; start < Queries; start += ScopeSize)
        {
#if REUSE_BOUNDS
            using HistoryReader.ReadScope scope = new();
#endif
            int end = Math.Min(start + ScopeSize, Queries);
            for (int i = start; i < end; i++)
            {
#if REUSE_BOUNDS
                int written = _store.TryGetValueBeforeNextChange(3, _keys[i], output, out _, scope.Accounts, generation: 0);
#else
                int written = _store.TryGetValueBeforeNextChange(3, _keys[i], output, out _);
#endif
                if (written > 0) checksum += output[3];
            }
        }
        return checksum;
    }

    private static byte[] Key(int group)
    {
        byte[] key = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(key, group);
        return key;
    }

    private static byte[] Value(int group, ulong version)
    {
        byte[] value = new byte[64];
        BinaryPrimitives.WriteInt32BigEndian(value, group);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(32), version);
        return value;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class BenchmarkHardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 1L << 30;
        public int? MaxOpenFilesLimit => null;
    }
}
