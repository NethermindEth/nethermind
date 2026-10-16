using System.Diagnostics;
using System.Runtime;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

ManualConfig config = ManualConfig.Create(DefaultConfig.Instance);
config.BuildTimeout = TimeSpan.FromMinutes(10);
IToolchain toolchain = CsProjCoreToolchain.NetCoreApp10_0;
config.AddJob(Job.Default
    .WithRuntime(CoreRuntime.Core10_0)
    .WithToolchain(new Toolchain(toolchain.Name,
        new ExactProjectGenerator(Path.GetFullPath("benchmarks/blob-batch/BlobBatch.csproj")),
        toolchain.Builder, toolchain.Executor))
    .WithLaunchCount(1).WithWarmupCount(3).WithIterationCount(8));
BenchmarkSwitcher.FromAssembly(typeof(BlobBatchBenchmarks).Assembly).Run(args, config);

Console.WriteLine($"Runtime={Environment.Version} ServerGC={GCSettings.IsServerGC} LatencyMode={GCSettings.LatencyMode}");
Console.WriteLine("count,hits,round,mode,no_gc,ns_per_op,bytes_per_op,gen0,gen1,gen2");
foreach ((int count, int hits) in new[] { (1, 1), (2, 1), (2, 2), (16, 8) })
{
    BlobBatchBenchmarks probe = new() { Count = count, HitPercent = hits * 100 / count };
    probe.Setup();
    try
    {
        string[] modes = ["direct", "scalar-copy", "span-copy", "pooled-copy", "keep-alive"];
        foreach (bool noGc in new[] { false, true })
        {
            for (int round = 0; round < 4; round++)
            {
                foreach (string mode in round % 2 == 0 ? modes : modes.Reverse())
                {
                    for (int i = 0; i < 256; i++)
                        if (probe.Run(mode) != hits) throw new Exception("Wrong warmup hit count");
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    bool started = noGc && GC.TryStartNoGCRegion(256L << 20, 200L << 20, disallowFullBlockingGC: false);
                    if (noGc && !started) { Console.WriteLine($"SKIP no-GC count={count} mode={mode}"); continue; }
                    int iterations = noGc ? Math.Max(16, 128 / hits) : Math.Max(256, 4096 / hits);
                    long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    long start = Stopwatch.GetTimestamp();
                    int found = 0;
                    for (int i = 0; i < iterations; i++) found += probe.Run(mode);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                    g0 = GC.CollectionCount(0) - g0; g1 = GC.CollectionCount(1) - g1; g2 = GC.CollectionCount(2) - g2;
                    if (started) GC.EndNoGCRegion();
                    if (found != hits * iterations || (noGc && (g0 != 0 || g1 != 0 || g2 != 0))) throw new Exception("Invalid measurement");
                    Console.WriteLine(FormattableString.Invariant($"{count},{hits},{round},{mode},{noGc},{elapsed * 1e9 / Stopwatch.Frequency / iterations:F1},{allocated / iterations},{g0},{g1},{g2}"));
                }
            }
        }
    }
    finally { probe.Cleanup(); }
}

public class BlobBatchBenchmarks
{
    private const int Records = 128;
    private const RlpBehaviors Behaviors = RlpBehaviors.InMempoolForm | RlpBehaviors.Storage;
    private ColumnsDb<BlobTxsColumns> _db = null!;
    private BlobTxStorage _storage = null!;
    private TxLookupKey[] _keys = null!;
    private Transaction?[] _results = null!;
    private Transaction?[] _expected = null!;
    private string _root = null!;
    private IDb _column = null!;
    private byte[] _packed = null!;

    public int Count { get; set; }

    public int HitPercent { get; set; }

    public void Setup()
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("BENCH_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new(_root, new DbSettings(DbNames.BlobTransactions, "blobs"), config, factory,
            LimboLogs.Instance, Enum.GetValues<BlobTxsColumns>());
        _storage = new(_db);
        _column = _db.GetColumnDb(BlobTxsColumns.FullBlobTxs);
        _packed = new byte[Count * 64];
        Transaction[] stored = new Transaction[Records];
        Random random = new(1701);
        Address sender = new(new byte[Address.Size]);
        for (int i = 0; i < Records; i++)
        {
            byte[] blob = new byte[131072];
            random.NextBytes(blob);
            byte[] versionedHash = new byte[32];
            random.NextBytes(versionedHash);
            versionedHash[0] = 1;
            Transaction input = new()
            {
                Type = TxType.Blob, Nonce = (ulong)i, GasLimit = 21000, GasPrice = 1,
                DecodedMaxFeePerGas = 2, MaxFeePerBlobGas = 1, Value = 1, To = sender,
                ChainId = 1, Signature = new Signature((UInt256)1, (UInt256)1, 27),
                BlobVersionedHashes = [versionedHash],
                NetworkWrapper = new ShardBlobNetworkWrapper([blob], [new byte[48]], [new byte[48]], ProofVersion.V0)
            };
            using ArrayPoolSpan<byte> encoded = TxDecoder.Instance.EncodeToArrayPoolSpan(input, Behaviors);
            RlpReader reader = new(encoded);
            Transaction tx = Rlp.Decode<Transaction>(ref reader, Behaviors)!;
            tx.Timestamp = (ulong)(i + 17);
            tx.SenderAddress = sender;
            stored[i] = tx;
            _storage.Add(tx);
        }
        _db.Flush();
        _keys = new TxLookupKey[Count];
        _results = new Transaction?[Count];
        _expected = new Transaction?[Count];
        int hits = Count * HitPercent / 100;
        for (int i = 0; i < Count; i++)
        {
            Transaction tx = stored[(i * 17 + 3) % Records];
            _keys[i] = new(tx.Hash!, sender, i < hits ? tx.Timestamp : tx.Timestamp + 10000);
            _keys[i].Timestamp.ToBigEndian(_packed.AsSpan(i * 64, 32));
            _keys[i].Hash.Bytes.CopyTo(_packed.AsSpan(i * 64 + 32, 32));
            if (i < hits) _expected[i] = tx;
        }
        Verify();
    }

    private void Verify()
    {
        int found = _storage.TryGetMany(_keys, Count, _results);
        if (found != Count * HitPercent / 100) throw new InvalidOperationException("Wrong hit count");
        for (int i = 0; i < Count; i++)
        {
            Transaction? expected = _expected[i];
            Transaction? actual = _results[i];
            if (expected is null)
            {
                if (actual is not null) throw new InvalidOperationException("Missing key returned a transaction");
                continue;
            }
            if (actual is null || actual.Hash != expected.Hash || actual.SenderAddress != expected.SenderAddress
                || actual.Timestamp != expected.Timestamp) throw new InvalidOperationException("Wrong transaction metadata");
            using ArrayPoolSpan<byte> expectedBytes = TxDecoder.Instance.EncodeToArrayPoolSpan(expected, Behaviors);
            using ArrayPoolSpan<byte> actualBytes = TxDecoder.Instance.EncodeToArrayPoolSpan(actual, Behaviors);
            if (!((ReadOnlySpan<byte>)expectedBytes).SequenceEqual(actualBytes))
                throw new InvalidOperationException("Transaction bytes differ");
        }
        Console.WriteLine($"Verified full payloads and metadata: count={Count}, hits={found}");
    }

    public int Run(string mode)
    {
        switch (mode)
        {
            case "direct": return _storage.TryGetMany(_keys, Count, _results);
            case "keep-alive":
                Transaction? previous = _results[0];
                int found = _storage.TryGetMany(_keys, Count, _results);
                GC.KeepAlive(previous);
                return found;
            case "scalar-copy":
                int scalarFound = 0;
                for (int i = 0; i < Count; i++)
                    if (_storage.TryGet(_keys[i].Hash, _keys[i].Sender, _keys[i].Timestamp, out _results[i])) scalarFound++;
                return scalarFound;
            case "pooled-copy":
                _column.GetMany<Transaction?, PooledDecoder>(_packed, 64, _results, default);
                return _results.Count(static tx => tx is not null);
            case "span-copy":
                int spanFound = 0;
                for (int i = 0; i < Count; i++)
                {
                    ReadOnlySpan<byte> value = _column.GetSpan(_packed.AsSpan(i * 64, 64));
                    try
                    {
                        RlpReader reader = new(value);
                        _results[i] = value.IsEmpty ? null : Rlp.Decode<Transaction>(ref reader, Behaviors);
                        if (_results[i] is not null) spanFound++;
                    }
                    finally { _column.DangerousReleaseMemory(value); }
                }
                return spanFound;
            default: throw new ArgumentException(mode);
        }
    }

    private readonly struct PooledDecoder : IValueDecoder<Transaction?>
    {
        public Transaction? Decode(ReadOnlySpan<byte> bytes)
        {
            using ArrayPoolSpan<byte> buffer = new(bytes.Length);
            bytes.CopyTo(buffer);
            RlpReader reader = new((ReadOnlySpan<byte>)buffer);
            return Rlp.Decode<Transaction>(ref reader, Behaviors);
        }
    }

    public void Cleanup()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class HardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 1L << 30;
        public int? MaxOpenFilesLimit => null;
    }
}

