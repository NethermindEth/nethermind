// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
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
    public const int WorkItems = AccountPartitions + StorageRanges;

    private readonly IColumnsDb<FlatHistoryColumns> _history;
    private readonly ISortedKeyValueStore _accountHistory;
    private readonly ISortedKeyValueStore _storageHistory;
    private readonly IDb _availableBlocks;
    private readonly IHistoryHeaderSource _headers;
    private readonly ICommitmentEmitterSource? _emitterSource;
    private readonly long _maxRowsPerPartition;
    private readonly ulong _from;
    private readonly ulong _to;
    private readonly ulong _checkpointBlocks;
    private readonly Action<int, ulong>? _onCheckpoint;
    private readonly CancellationToken _token;
    private readonly ILogger _logger;
    private readonly MismatchSink _sink = new();
    private readonly CommitmentMetadata _metadata;
    private readonly WalkProgress _progress;
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
        ulong checkpointBlocks,
        Action<int, ulong>? onCheckpoint,
        CancellationToken token)
    {
        _history = history;
        _checkpointBlocks = checkpointBlocks;
        _onCheckpoint = onCheckpoint;
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
        _metadata = new CommitmentMetadata(history);
        _progress = new WalkProgress(_logger, WorkItems, from, to);
        _scanner = new HistoryRowScanner(_accountHistory, _storageHistory, storageClears, rowFormat);
        _accounts = new AccountSubtreeReplayer(_accountHistory, rowFormat, logManager);
        _storages = new StorageSubtreeReplayer(_accountHistory, _storageHistory, rowFormat, rlpWrapSlots, logManager, _sink);
        _combiner = new SubtreeCombiner(new SeriesReader(history), maxRowsPerPartition);
    }

    public HistoryWalkVerdict Execute(int workers)
    {
        bool resuming = _metadata.TryGetWalkInProgress(out ulong from, out ulong to) && from == _from && to == _to;
        using (SeriesWriter scratch = new(_history))
        {
            if (!resuming)
            {
                scratch.DeleteAllScratch();
                _metadata.BeginWalk(_from, _to, WorkItems);
            }
            else
            {
                for (int item = 0; item < WorkItems; item++)
                {
                    if (_metadata.IsWalkItemDone(item)) continue;

                    if (item < AccountPartitions) scratch.DeleteAccountScratchUnder((byte)item);
                    else scratch.DeleteStorageScratchUnder((byte)(item - AccountPartitions));
                }
            }
        }

        List<Action> partitions = [];
        int previouslyCompleted = 0;
        for (int nibbles = 0; nibbles < AccountPartitions; nibbles++)
        {
            int item = nibbles;
            if (_metadata.IsWalkItemDone(item))
            {
                previouslyCompleted++;
                _progress.PreviouslyCompleted(item);
                continue;
            }

            TreePath prefix = TreePath.FromNibble([(byte)(nibbles >> 4), (byte)(nibbles & 0x0F)]);
            partitions.Add(() =>
            {
                ProcessAccountPartition(prefix, item);
                _metadata.MarkWalkItemDone(item);
                _progress.Completed(item);
            });
        }

        for (int first = 0; first < StorageRanges; first++)
        {
            int item = AccountPartitions + first;
            if (_metadata.IsWalkItemDone(item))
            {
                previouslyCompleted++;
                _progress.PreviouslyCompleted(item);
                continue;
            }

            byte firstByte = (byte)first;
            partitions.Add(() =>
            {
                ProcessStorageRange(firstByte, item);
                _metadata.MarkWalkItemDone(item);
                _progress.Completed(item);
            });
        }

        if (resuming && _logger.IsInfo) _logger.Info($"History walk resuming: {previouslyCompleted} of {WorkItems} subtrees were finished before the restart, {partitions.Count} remain.");
        using (_progress)
        {
            _progress.Start();
            RunParallel(partitions, workers);

            if (_logger.IsInfo) _logger.Info($"History walk: all {WorkItems} subtrees replayed; folding the root and comparing every block in [{_from}, {_to}] to its header.");
            RootHeaderCheck root = new(_headers, _availableBlocks, _sink, _logger);
            using (CommitmentEmitter? emitter = _emitterSource?.CreateEmitter())
            using (SeriesWriter series = new(_history))
            {
                _combiner.CombineRoot((nibble, child) => AccountSeriesKey(TreePath.FromNibble([(byte)nibble, (byte)child])), _from, _to, emitter, series, root, _progress, _token);
                emitter?.FlushOpenWindows();
                series.DeleteAllScratch();
            }

            _metadata.ClearWalk(WorkItems);
            List<HistoryWalkMismatch> mismatches = _sink.Drain();
            return new HistoryWalkVerdict(mismatches.Count == 0, root.Compared, mismatches);
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

    private void ProcessAccountPartition(in TreePath prefix, int item)
    {
        using AccountPartitionRows rows = new();
        StoragePresenceProbe probe = new(_storageHistory);
        while (true)
        {
            List<HistoryWalkMismatch> local = [];
            StorageRootMoveCheck check = new(probe, local);
            ScanOutcome outcome = _scanner.ScanAccounts(prefix, _from, _to, _maxRowsPerPartition, rows, check, _token);
            if (outcome == ScanOutcome.SinglePathOverflow) continue;

            if (outcome == ScanOutcome.Split)
            {
                rows.Reset();
                for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
                {
                    _progress.EnterChild(item, nibble, BranchRlp.ChildCount);
                    ProcessAccountPartition(prefix.Append(nibble), item);
                    _progress.ExitChild(item);
                }

                CombineAccount(prefix);
                return;
            }

            ulong? resumeFrom = prefix.Length == AccountPartitionDepth && _metadata.TryGetWalkItemProgress(item, out ulong reached) ? reached : null;
            Action<ulong>? checkpoint = prefix.Length == AccountPartitionDepth ? block => Checkpoint(item, block) : null;
            using (CommitmentEmitter? emitter = _emitterSource?.CreateEmitter())
            using (SeriesWriter series = new(_history))
            {
                _accounts.Replay(prefix, rows, _from, _to, emitter, AccountSeriesKey(prefix), series, check, _progress, item, resumeFrom, _checkpointBlocks, checkpoint, _token);
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

    private void ProcessStorageRange(byte firstByte, int item)
    {
        uint? afterPrefix = _metadata.TryGetWalkItemProgress(item, out ulong done) ? (uint)done : null;
        _scanner.ScanStorageGroups(firstByte, _from, _to, _maxRowsPerPartition, afterPrefix, group =>
        {
            using (StoragePartitionRows rows = group.Rows)
            {
                if (group.Overflow)
                {
                    rows.Reset();
                    ProcessStoragePartition(group.Prefix, TreePath.Empty, group.Clears, identities: null, item);
                }
                else
                {
                    ReplayStorageGroup(TreePath.Empty, rows, group.Clears, item);
                }
            }

            Checkpoint(item, BinaryPrimitives.ReadUInt32BigEndian(group.Prefix));
        }, position => _progress.ScanningKeySpace(item, position, 1u << 24), _token);
    }

    private void Checkpoint(int item, ulong progress)
    {
        _metadata.MarkWalkItemProgress(item, progress);
        _onCheckpoint?.Invoke(item, progress);
    }

    private void ProcessStoragePartition(byte[] storagePrefix, in TreePath slotPrefix, List<ClearRecord> clears, HashSet<ValueHash256>? identities, int item)
    {
        using StoragePartitionRows rows = new();
        while (true)
        {
            ScanOutcome outcome = _scanner.ScanStorage(storagePrefix, slotPrefix, _from, _to, _maxRowsPerPartition, rows, clears, _token);
            if (outcome == ScanOutcome.SinglePathOverflow) continue;

            if (outcome == ScanOutcome.Fits)
            {
                identities?.UnionWith(rows.Identities);
                ReplayStorageGroup(slotPrefix, rows, clears, item);
                return;
            }

            break;
        }

        rows.Reset();
        HashSet<ValueHash256> seen = [];
        for (int nibble = 0; nibble < BranchRlp.ChildCount; nibble++)
        {
            ProcessStoragePartition(storagePrefix, slotPrefix.Append(nibble), clears, seen, item);
        }

        identities?.UnionWith(seen);
        foreach (ValueHash256 identity in seen)
        {
            CombineStorage(identity, slotPrefix);
        }
    }

    private void ReplayStorageGroup(in TreePath slotPrefix, StoragePartitionRows rows, List<ClearRecord> clears, int item)
    {
        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        _storages.Replay(slotPrefix, rows, clears, _from, _to, emitter, series, slotPrefix.Length > 0, _progress, item, _token);
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

        using ContractRootCheck? release = check;
        using CommitmentEmitter? emitter = _emitterSource?.CreateEmitter();
        using SeriesWriter series = new(_history);
        _combiner.Combine(scope, slotPrefix, nibble => scope.Key(parent.Append(nibble), scratch: true), slotPrefix.Length == 0 ? null : scope.Key(slotPrefix, scratch: true), _from, _to, emitter, series, check, _token);
        emitter?.FlushOpenWindows();
        check?.End();
    }
}
