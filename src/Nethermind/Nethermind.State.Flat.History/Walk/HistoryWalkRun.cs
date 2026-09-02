// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal sealed class HistoryWalkRun
{
    public const int AccountPartitionDepth = 2;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly ISortedKeyValueStore _accountHistory;
    private readonly ISortedKeyValueStore _storageHistory;
    private readonly ISortedKeyValueStore _storageClears;
    private readonly IDb _availableBlocks;
    private readonly IHistoryHeaderSource _headers;
    private readonly HistoryRowFormat _rowFormat;
    private readonly ICommitmentEmitterSource? _emitterSource;
    private readonly long _maxRowsPerPartition;
    private readonly ulong _from;
    private readonly ulong _to;
    private readonly CancellationToken _token;
    private readonly ILogger _logger;
    private readonly MismatchSink _sink = new();
    private readonly SeriesReader _reader;
    private readonly HistoryRowScanner _scanner;
    private readonly AccountSubtreeReplayer _accounts;
    private readonly StorageSubtreeReplayer _storages;
    private readonly SubtreeCombiner _combiner;

    public HistoryWalkRun(
        IColumnsDb<FlatHistoryColumns> history,
        IHistoryHeaderSource headers,
        HistoryRowFormat rowFormat,
        bool rlpWrapSlots,
        ILogManager logManager,
        long maxRowsPerPartition,
        ICommitmentEmitterSource? emitterSource,
        ulong from,
        ulong to,
        CancellationToken token)
    {
        _history = history;
        _accountHistory = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.AccountHistory);
        _storageHistory = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageHistory);
        _storageClears = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageClears);
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _headers = headers;
        _rowFormat = rowFormat;
        _emitterSource = emitterSource;
        _maxRowsPerPartition = maxRowsPerPartition;
        _from = from;
        _to = to;
        _token = token;
        _logger = logManager.GetClassLogger<HistoryWalkVerifier>();
        _reader = new SeriesReader(history);
        _scanner = new HistoryRowScanner(_accountHistory, _storageHistory, _storageClears, rowFormat);
        _accounts = new AccountSubtreeReplayer(_accountHistory, rowFormat, logManager);
        _storages = new StorageSubtreeReplayer(_storageHistory, rowFormat, rlpWrapSlots, logManager);
        _combiner = new SubtreeCombiner(_reader);
    }

    public ulong BlocksCompared { get; private set; }

    public HistoryWalkVerdict Execute(int workers)
    {
        try
        {
            List<Action> partitions = [];
            for (int nibbles = 0; nibbles < 256; nibbles++)
            {
                TreePath prefix = TreePath.FromNibble([(byte)(nibbles >> 4), (byte)(nibbles & 0x0F)]);
                partitions.Add(() => ProcessAccountPartition(prefix));
            }

            for (int first = 0; first < 256; first++)
            {
                byte firstByte = (byte)first;
                partitions.Add(() => ProcessStorageRange(firstByte));
            }

            RunParallel(partitions, workers);

            List<Action> level = [];
            for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
            {
                TreePath parent = TreePath.FromNibble([(byte)nibble]);
                level.Add(() => CombineAccount(parent, observer: null));
            }

            RunParallel(level, workers);

            RootHeaderCheck root = new(_headers, _availableBlocks, _sink, _logger);
            CombineAccount(TreePath.Empty, root);
            BlocksCompared = root.Compared;
        }
        finally
        {
            using SeriesWriter scratch = new(_history);
            scratch.DeleteAllScratch();
        }

        List<HistoryWalkMismatch> mismatches = _sink.Drain();
        return new HistoryWalkVerdict(mismatches.Count == 0, BlocksCompared, mismatches);
    }

    private void RunParallel(List<Action> items, int workers)
    {
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = _token };
        try
        {
            Parallel.ForEach(items, options, static item => item());
        }
        catch (AggregateException e) when (e.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
        }
    }

    private void ProcessAccountPartition(in TreePath prefix)
    {
        AccountPartitionRows rows = new();
        StoragePresenceProbe probe = new(_storageHistory, _storageClears, _rowFormat);
        while (true)
        {
            List<HistoryWalkMismatch> local = [];
            StorageRootMoveCheck check = new(probe, _to, local);
            ScanOutcome outcome = _scanner.ScanAccounts(prefix, _from, _to, _maxRowsPerPartition, rows, check, _token);
            if (outcome == ScanOutcome.SinglePathOverflow) continue;

            if (outcome == ScanOutcome.Split)
            {
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++) ProcessAccountPartition(prefix.Append(nibble));
                CombineAccount(prefix, observer: null);
                return;
            }

            using (CommitmentEmitter? emitter = _emitterSource?.CreateEmitter())
            using (SeriesWriter series = new(_history))
            {
                _accounts.Replay(prefix, rows, _from, _to, emitter, prefix.Length == 0 ? null : AccountSeriesKey(prefix), series, check, _token);
                emitter?.FlushOpenWindows();
            }

            _sink.AddRange(local);
            return;
        }
    }

    private void CombineAccount(in TreePath parent, ViewObserver? observer)
    {
        TreePath path = parent;
        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        _combiner.Combine(
            isStorage: false,
            default,
            parent,
            nibble => AccountSeriesKey(path.Append(nibble)),
            parent.Length == 0 ? null : AccountSeriesKey(parent),
            _from,
            _to,
            emitter,
            series,
            observer,
            _token);
        emitter?.FlushOpenWindows();
    }

    private SeriesKey AccountSeriesKey(in TreePath path)
    {
        bool real = _emitterSource is not null && path.Length <= _emitterSource.Policy.AccountExactDepth;
        return new SeriesKey(isStorage: false, default, path, scratch: !real);
    }

    private static SeriesKey StorageSeriesKey(in ValueHash256 identity, in TreePath slotPrefix) =>
        new(isStorage: true, identity, slotPrefix, scratch: true);

    private void ProcessStorageRange(byte firstByte)
    {
        Span<byte> lower = stackalloc byte[HistoryRowScanner.StorageRowKeyLength];
        lower.Clear();
        lower[0] = firstByte;
        Span<byte> upper = stackalloc byte[HistoryRowScanner.StorageRowKeyLength + 1];
        upper.Clear();
        if (firstByte == byte.MaxValue) upper.Fill(0xFF);
        else upper[0] = (byte)(firstByte + 1);

        using ISortedView view = _storageHistory.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss);
        byte[] currentPrefix = new byte[HistoryRowScanner.StoragePrefixLength];
        bool haveGroup = false;
        bool overflow = false;
        StoragePartitionRows? rows = null;
        StorageRowCollector? collector = null;
        List<ClearRecord>? clears = null;

        while (view.MoveNext())
        {
            _token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != HistoryRowScanner.StorageRowKeyLength) continue;

            ReadOnlySpan<byte> prefix = key[..HistoryRowScanner.StoragePrefixLength];
            if (!haveGroup || !prefix.SequenceEqual(currentPrefix))
            {
                if (haveGroup) FinishGroup(currentPrefix, overflow, rows!, clears!);

                prefix.CopyTo(currentPrefix);
                haveGroup = true;
                overflow = false;
                clears = _scanner.LoadClears(currentPrefix, _to);
                rows = new StoragePartitionRows();
                collector = new StorageRowCollector(rows, clears, _from, _to, _maxRowsPerPartition, _rowFormat);
            }

            if (overflow) continue;
            if (!collector!.TryAdd(key, view.CurrentValue)) overflow = true;
        }

        if (haveGroup) FinishGroup(currentPrefix, overflow, rows!, clears!);
    }

    private void FinishGroup(byte[] storagePrefix, bool overflow, StoragePartitionRows rows, List<ClearRecord> clears)
    {
        if (overflow)
        {
            ProcessStoragePartition(storagePrefix, TreePath.Empty, clears, identities: null);
            return;
        }

        ReplayStorageGroup(TreePath.Empty, rows, clears);
    }

    private void ProcessStoragePartition(byte[] storagePrefix, in TreePath slotPrefix, List<ClearRecord> clears, HashSet<ValueHash256>? identities)
    {
        StoragePartitionRows rows = new();
        while (true)
        {
            ScanOutcome outcome = _scanner.ScanStorage(storagePrefix, slotPrefix, _from, _to, _maxRowsPerPartition, rows, clears, _token);
            if (outcome == ScanOutcome.SinglePathOverflow) continue;

            if (outcome == ScanOutcome.Fits)
            {
                identities?.UnionWith(rows.Identities);
                ReplayStorageGroup(slotPrefix, rows, clears);
                return;
            }

            break;
        }

        HashSet<ValueHash256> seen = [];
        foreach (ClearRecord clear in clears)
        {
            if (clear.Block > _from && clear.Block <= _to) seen.Add(clear.Identity);
        }

        for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
        {
            ProcessStoragePartition(storagePrefix, slotPrefix.Append(nibble), clears, seen);
        }

        identities?.UnionWith(seen);
        foreach (ValueHash256 identity in seen)
        {
            CombineStorage(identity, slotPrefix);
        }
    }

    private void ReplayStorageGroup(in TreePath slotPrefix, StoragePartitionRows rows, List<ClearRecord> clears)
    {
        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        if (slotPrefix.Length == 0)
        {
            ContractRootCheck check = new(_accountHistory, _rowFormat, _sink);
            _storages.Replay(slotPrefix, rows, clears, _from, _to, emitter, null, series, check, _token);
        }
        else
        {
            TreePath prefix = slotPrefix;
            _storages.Replay(slotPrefix, rows, clears, _from, _to, emitter, identity => StorageSeriesKey(identity, prefix), series, null, _token);
        }

        emitter?.FlushOpenWindows();
    }

    private void CombineStorage(in ValueHash256 identity, in TreePath slotPrefix)
    {
        ValueHash256 scope = identity;
        TreePath parent = slotPrefix;
        ContractRootCheck? check = null;
        if (slotPrefix.Length == 0)
        {
            check = new ContractRootCheck(_accountHistory, _rowFormat, _sink);
            check.Begin(identity, _from, _to);
        }

        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        _combiner.Combine(
            isStorage: true,
            identity,
            slotPrefix,
            nibble => StorageSeriesKey(scope, parent.Append(nibble)),
            slotPrefix.Length == 0 ? null : StorageSeriesKey(identity, slotPrefix),
            _from,
            _to,
            emitter,
            series,
            check,
            _token);
        emitter?.FlushOpenWindows();
        check?.End();
    }
}
