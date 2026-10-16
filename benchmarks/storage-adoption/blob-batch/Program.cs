using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CsProj;
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
    .WithLaunchCount(1).WithWarmupCount(15).WithIterationCount(8));
BenchmarkSwitcher.FromAssembly(typeof(BlobBatchBenchmarks).Assembly).Run(args, config);

[MemoryDiagnoser]
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

    [Params(1, 2, 16, 64)]
    public int Count { get; set; }

    [Params(0, 50, 100)]
    public int HitPercent { get; set; }

    [Params("active", "deleted")]
    public string DataState { get; set; } = "active";

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Environment.GetEnvironmentVariable("BENCH_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        DbConfig config = new() { BlobTransactionsDbAdditionalRocksDbOptions = "disable_auto_compactions=true;" };
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new(_root, new DbSettings(DbNames.BlobTransactions, "blobs"), config, factory,
            LimboLogs.Instance, Enum.GetValues<BlobTxsColumns>());
        _storage = new(_db);
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
        HashSet<Hash256> retained = new();
        for (int i = 0; i < Count; i++)
        {
            Transaction tx = stored[(i * 17 + 3) % Records];
            _keys[i] = new(tx.Hash!, sender, i < hits || DataState == "deleted" ? tx.Timestamp : tx.Timestamp + 10000);
            if (i < hits)
            {
                _expected[i] = tx;
                retained.Add(tx.Hash!);
            }
        }
        if (DataState == "deleted")
        {
            if (_storage.TryGetMany(_keys, Count, _results) != Count)
                throw new InvalidOperationException("Every deletion-case key must exist before deletion.");
            foreach (Transaction tx in stored)
            {
                if (!retained.Contains(tx.Hash!)) _storage.Delete(tx.Hash!.ValueHash256, tx.Timestamp);
            }
            _db.Flush();
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

    [Benchmark]
    public int ReadBlobs() => _storage.TryGetMany(_keys, Count, _results);

    [GlobalCleanup]
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

sealed class ExactProjectGenerator(string path) : CsProjGenerator("net10.0", null!, null!, null!)
{
    protected override FileInfo GetProjectFilePath(Type target, BenchmarkDotNet.Loggers.ILogger logger) => new(path);
}
