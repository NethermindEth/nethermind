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
    private const int AccountPartitionDepth = 2;
    private const int AccountPartitions = 1 << (4 * AccountPartitionDepth);
    private const int StorageRanges = 256;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly ISortedKeyValueStore _accountHistory;
    private readonly ISortedKeyValueStore _storageHistory;
    private readonly IDb _availableBlocks;
    private readonly IHistoryHeaderSource _headers;
    private readonly ICommitmentEmitterSource? _emitterSource;
    private readonly long _maxRowsPerPartition;
    private readonly ulong _from;
    private readonly ulong _to;
    private readonly CancellationToken _token;
    private readonly ILogger _logger;
    private readonly MismatchSink _sink = new();
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
        ISortedKeyValueStore storageClears = (ISortedKeyValueStore)history.GetColumnDb(FlatHistoryColumns.StorageClears);
        _availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);
        _headers = headers;
        _emitterSource = emitterSource;
        _maxRowsPerPartition = maxRowsPerPartition;
        _from = from;
        _to = to;
        _token = token;
        _logger = logManager.GetClassLogger<HistoryWalkVerifier>();
        _scanner = new HistoryRowScanner(_accountHistory, _storageHistory, storageClears, rowFormat);
        _accounts = new AccountSubtreeReplayer(_accountHistory, rowFormat, logManager);
        _storages = new StorageSubtreeReplayer(_accountHistory, _storageHistory, rowFormat, rlpWrapSlots, logManager, _sink);
        _combiner = new SubtreeCombiner(new SeriesReader(history));
    }

    public HistoryWalkVerdict Execute(int workers)
    {
        ulong compared;
        DeleteScratch();
        try
        {
            List<Action> partitions = [];
            for (int nibbles = 0; nibbles < AccountPartitions; nibbles++)
            {
                TreePath prefix = TreePath.FromNibble([(byte)(nibbles >> 4), (byte)(nibbles & 0x0F)]);
                partitions.Add(() => ProcessAccountPartition(prefix));
            }

            for (int first = 0; first < StorageRanges; first++)
            {
                byte firstByte = (byte)first;
                partitions.Add(() => ProcessStorageRange(firstByte));
            }

            RunParallel(partitions, workers);

            RootHeaderCheck root = new(_headers, _availableBlocks, _sink, _logger);
            using (CommitmentEmitter? emitter = _emitterSource?.CreateEmitter())
            using (SeriesWriter series = new(_history))
            {
                _combiner.CombineRoot((nibble, child) => AccountSeriesKey(TreePath.FromNibble([(byte)nibble, (byte)child])), _from, _to, emitter, series, root, _token);
                emitter?.FlushOpenWindows();
            }

            compared = root.Compared;
        }
        finally
        {
            DeleteScratch();
        }

        List<HistoryWalkMismatch> mismatches = _sink.Drain();
        return new HistoryWalkVerdict(mismatches.Count == 0, compared, mismatches);
    }

    private void DeleteScratch()
    {
        try
        {
            using SeriesWriter scratch = new(_history);
            scratch.DeleteAllScratch();
        }
        catch (ObjectDisposedException)
        {
        }
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
        AccountPartitionRows? rows = new();
        StoragePresenceProbe probe = new(_storageHistory);
        while (true)
        {
            List<HistoryWalkMismatch> local = [];
            StorageRootMoveCheck check = new(probe, local);
            ScanOutcome outcome = _scanner.ScanAccounts(prefix, _from, _to, _maxRowsPerPartition, rows, check, _token);
            if (outcome == ScanOutcome.SinglePathOverflow) continue;

            if (outcome == ScanOutcome.Split)
            {
                rows = null;
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++) ProcessAccountPartition(prefix.Append(nibble));
                CombineAccount(prefix);
                return;
            }

            using (CommitmentEmitter? emitter = _emitterSource?.CreateEmitter())
            using (SeriesWriter series = new(_history))
            {
                _accounts.Replay(prefix, rows, _from, _to, emitter, AccountSeriesKey(prefix), series, check, _token);
                emitter?.FlushOpenWindows();
            }

            _sink.AddRange(local);
            return;
        }
    }

    private void CombineAccount(in TreePath parent)
    {
        TreePath path = parent;
        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        _combiner.Combine(SeriesScope.Accounts, parent, nibble => AccountSeriesKey(path.Append(nibble)), AccountSeriesKey(parent), _from, _to, emitter, series, observer: null, _token);
        emitter?.FlushOpenWindows();
    }

    private SeriesKey AccountSeriesKey(in TreePath path)
    {
        bool real = _emitterSource is not null && _emitterSource.Policy.IsExactAccountDepth(path.Length);
        return SeriesScope.Accounts.Key(path, scratch: !real);
    }

    private void ProcessStorageRange(byte firstByte) =>
        _scanner.ScanStorageGroups(firstByte, _from, _to, _maxRowsPerPartition, group =>
        {
            if (group.Overflow) ProcessStoragePartition(group.Prefix, TreePath.Empty, group.Clears, identities: null);
            else ReplayStorageGroup(TreePath.Empty, group.Rows, group.Clears);
        }, _token);

    private void ProcessStoragePartition(byte[] storagePrefix, in TreePath slotPrefix, List<ClearRecord> clears, HashSet<ValueHash256>? identities)
    {
        StoragePartitionRows? rows = new();
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

        rows = null;
        HashSet<ValueHash256> seen = [];
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
        _storages.Replay(slotPrefix, rows, clears, _from, _to, emitter, series, writeSeries: slotPrefix.Length > 0, _token);
        emitter?.FlushOpenWindows();
    }

    private void CombineStorage(in ValueHash256 identity, in TreePath slotPrefix)
    {
        SeriesScope scope = SeriesScope.Storage(identity);
        TreePath parent = slotPrefix;
        ContractRootCheck? check = null;
        if (slotPrefix.Length == 0)
        {
            check = new ContractRootCheck(_accountHistory, _scanner.RowFormat, _sink);
            check.Begin(identity, _from, _to, _token);
        }

        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        _combiner.Combine(scope, slotPrefix, nibble => scope.Key(parent.Append(nibble), scratch: true), slotPrefix.Length == 0 ? null : scope.Key(slotPrefix, scratch: true), _from, _to, emitter, series, check, _token);
        emitter?.FlushOpenWindows();
        check?.End();
    }
}
