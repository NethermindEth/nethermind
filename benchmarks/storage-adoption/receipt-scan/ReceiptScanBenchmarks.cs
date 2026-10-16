// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Reflection;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;

[MemoryDiagnoser]
public class ReceiptScanBenchmarks
{
    private static readonly ScanLogs Scan = typeof(LogFinder)
        .GetMethod("FilterLogsInBlockLowMemoryAllocation", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<ScanLogs>();

    private ColumnsDb<ReceiptsColumns> _root = null!;
    private IDb _db = null!;
    private string _directory = null!;
    private byte[] _key = null!;
    private Block _block = null!;
    private TxReceipt[] _receipts = null!;
    private LogFilter _filter = null!;
    private ReceiptsRecovery _recovery = null!;
    private IReceiptRefDecoder _decoder = null!;
    private bool _compact;

    [Params(1, 128)]
    public int ReceiptCount { get; set; }

    [Params(32, 4096)]
    public int DataSize { get; set; }

    [Params("full-dense", "compact-hashes-dense", "compact-legacy-dense", "compact-hashes-sparse", "missing", "empty")]
    public string Shape { get; set; } = "full-dense";

    [GlobalSetup]
    public void Setup()
    {
        string mode = Environment.GetEnvironmentVariable("RECEIPT_READ_MODE")!;
#if DECODED_RECEIPTS
        if (mode != "decoded") throw new InvalidOperationException("Wrong decoded build mode.");
#else
        if (mode != "legacy") throw new InvalidOperationException("Wrong legacy build mode.");
#endif
        Transaction[] transactions = new Transaction[ReceiptCount];
        for (int i = 0; i < transactions.Length; i++)
            transactions[i] = Build.A.Transaction.WithNonce((ulong)i).TestObject;
        _block = Build.A.Block.WithNumber(25_490_000).WithTimestamp(1_770_000_000)
            .WithTransactions(transactions).TestObject;
        _receipts = new TxReceipt[ReceiptCount];
        bool sparse = Shape.EndsWith("sparse", StringComparison.Ordinal);
        for (int i = 0; i < _receipts.Length; i++)
        {
            byte[] data = new byte[DataSize];
            data.AsSpan().Fill((byte)(i + 1));
            Address address = !sparse || i % 16 == 0 ? TestItem.AddressA : TestItem.AddressB;
            LogEntry accepted = new(address, data, [TestItem.KeccakA]);
            LogEntry rejected = new(TestItem.AddressB, data, [TestItem.KeccakB]);
            _receipts[i] = Build.A.Receipt.WithAllFieldsFilled
                .WithBlockHash(_block.Hash).WithBlockNumber(_block.Number).WithIndex(i)
                .WithTransactionHash(transactions[i].Hash).WithGasUsedTotal((ulong)(i + 1) * 21_000)
                .WithLogs(accepted, rejected).TestObject;
        }
        _compact = Shape.StartsWith("compact", StringComparison.Ordinal);
        _decoder = _compact ? CompactReceiptStorageDecoder.Instance : new ReceiptStorageDecoder();
        ReceiptArrayStorageDecoder encoder = new(_compact, !Shape.Contains("legacy", StringComparison.Ordinal));
        byte[] encoded = encoder.Encode(_receipts, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts).Bytes;
        _key = new byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(_block.Number, _block.Hash!, _key);
        _filter = new LogFilter(1, new BlockParameter(_block.Number), new BlockParameter(_block.Number),
            new AddressFilter(TestItem.AddressA), SequenceTopicsFilter.AnyTopic);
        _recovery = new ReceiptsRecovery(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId), MainnetSpecProvider.Instance);
        _directory = Path.Combine(Environment.GetEnvironmentVariable("BENCH_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        Open();
        if (Shape != "missing") _db.PutSpan(_key, Shape == "empty" ? [] : encoded);
        _root.Flush();
        FilterLog[] owned = ReadLogs().ToArray();
        _root.Dispose();
        Verify(owned, sparse);
        Open();
        Console.WriteLine($"Verified owned logs after DB disposal: mode={mode}, shape={Shape}, receipts={ReceiptCount}, bytes={encoded.Length}, returned={owned.Length}");
    }

    private void Open()
    {
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _root = new ColumnsDb<ReceiptsColumns>(_directory, new DbSettings(DbNames.Receipts, "receipts"),
            config, factory, LimboLogs.Instance, Enum.GetValues<ReceiptsColumns>());
        _db = _root.GetColumnDb(ReceiptsColumns.Blocks);
    }

    private void Verify(FilterLog[] actual, bool sparse)
    {
        int expectedCount = Shape is "missing" or "empty" ? 0 : sparse ? (ReceiptCount + 15) / 16 : ReceiptCount;
        if (actual.Length != expectedCount) throw new InvalidOperationException("Wrong returned log count.");
        for (int i = 0; i < actual.Length; i++)
        {
            int receiptIndex = sparse ? i * 16 : i;
            FilterLog log = actual[i];
            LogEntry expected = _receipts[receiptIndex].Logs![0];
            if (log.LogIndex != receiptIndex * 2 || log.TransactionIndex != receiptIndex
                || log.TransactionHash != _block.Transactions[receiptIndex].Hash
                || log.BlockHash != _block.Hash || log.BlockNumber != _block.Number || log.BlockTimestamp != _block.Timestamp
                || log.Address != expected.Address || log.Removed
                || !log.Data.AsSpan().SequenceEqual(expected.Data)
                || !log.Topics.AsSpan().SequenceEqual(expected.Topics))
                throw new InvalidOperationException($"Wrong or borrowed log fields at {i}.");
        }
    }

    [Benchmark]
    public IEnumerable<FilterLog> ReadLogs()
    {
#if DECODED_RECEIPTS
        IEnumerable<FilterLog>? result = null;
        _db.GetMany<IEnumerable<FilterLog>?, ReceiptDecoder>(_key, _key.Length,
            MemoryMarshal.CreateSpan(ref result, 1), new ReceiptDecoder(this));
        return result ?? [];
#else
        Span<byte> value = _db.GetSpan(_key);
        return Filter(value, _db);
#endif
    }

    private IEnumerable<FilterLog> Filter(Span<byte> value, IDb? owner)
    {
        ReceiptsIterator iterator = _compact
            ? new ReceiptsIterator(value, owner!, _decoder,
                _recovery.CreateDeferredLogRecoveryContext(new BlockSource(_block)), _block.Hash!, _block.Number)
            : new ReceiptsIterator(value, owner!, null, _decoder);
        return Scan(_filter, ref iterator, _block.Hash!, _block.Number, _block.Timestamp, false, CancellationToken.None);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _root.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

#if DECODED_RECEIPTS
    private readonly struct ReceiptDecoder(ReceiptScanBenchmarks benchmark) : IValueDecoder<IEnumerable<FilterLog>?>
    {
        public IEnumerable<FilterLog> Decode(ReadOnlySpan<byte> value)
        {
            Span<byte> borrowed = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(value), value.Length);
            return benchmark.Filter(borrowed, null);
        }
    }
#endif

    private readonly struct BlockSource(Block block) : IReceiptsRecovery.IRecoveryBlockSource
    {
        public ReceiptRecoveryBlock GetBlock() => new(block);
    }

    private sealed class HardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 1L << 30;
        public int? MaxOpenFilesLimit => null;
    }

    private delegate IEnumerable<FilterLog> ScanLogs(LogFilter filter, ref ReceiptsIterator iterator,
        Hash256 hash, ulong number, ulong timestamp, bool logIndexPerReceipt, CancellationToken cancellationToken);
}
