// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Verkle;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.RangeSync;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Synchronization.VerkleSync;

public class VerkleProgressTracker: IRangeProgressTracker<VerkleSyncBatch>
{
    private const string NO_REQUEST = "NO REQUEST";
    private readonly byte[] SYNC_PROGRESS_KEY = "VerkleSyncProgressKey"u8.ToArray();

    // This does not need to be a lot as it spawn other requests. In fact 8 is probably too much. It is severely
    // bottlenecked by _syncCommit lock in SnapProviderHelper, which in turns is limited by the IO.
    // In any case, all partition will be touched when calculating progress, so we can't really put like 1024 for this.
    private readonly int _subTreeRangePartitionCount;

    private long _reqCount;
    private int _activeSubTreeRequests;
    private int _activeLeafRefreshRequests;

    private readonly ILogger _logger;
    private readonly IDb _db;

    // Partitions are indexed by its limit keccak/address as they are keep in the request struct and remain the same
    // throughout the sync. So its easy.
    private Dictionary<Stem, SubTreeRangePartition> SubTreeRangePartitions { get; set; } = new();

    // Using a queue here to evenly distribute request across partitions. Don't want a situation where one really slow
    // partition is taking up most of the time at the end of the sync.
    private ConcurrentQueue<SubTreeRangePartition> SubTreeRangeReadyForRequest { get; set; } = new();
    private ConcurrentQueue<byte[]> LeafsToRefresh { get; set; } = new();


    private readonly RangeSync.Pivot _pivot;

    public VerkleProgressTracker(IBlockTree blockTree, IDb db, ILogManager logManager, int subTreeRangePartitionCount = 8)
    {
        _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _db = db ?? throw new ArgumentNullException(nameof(db));

        _pivot = new RangeSync.Pivot(blockTree, logManager);

        if (subTreeRangePartitionCount < 1 || subTreeRangePartitionCount > 256)
            throw new ArgumentException("SubTree range partition must be between 1 to 256.");

        _subTreeRangePartitionCount = subTreeRangePartitionCount;
        SetupSubTreeRangePartition();

        //TODO: maybe better to move to a init method instead of the constructor
        GetSyncProgress();
    }

    private void SetupSubTreeRangePartition()
    {
        // Confusingly dividing the range evenly via UInt256 for example, consistently cause root hash mismatch.
        // The mismatch happens on exactly the same partition every time, suggesting tome kind of boundary issues
        // either on proof generation or validation.
        byte curStartingPath = 0;
        int partitionSize = (256 / _subTreeRangePartitionCount);

        for (int i = 0; i < _subTreeRangePartitionCount; i++)
        {
            SubTreeRangePartition partition = new SubTreeRangePartition();

            Stem startingPath = new(Stem.Zero.Bytes.ToArray()) { Bytes = { [0] = curStartingPath } };

            partition.NextSubTreePath = startingPath.Bytes;
            partition.SubTreePathStart = startingPath.Bytes;

            curStartingPath += (byte)partitionSize;

            // Special case for the last partition
            Stem limitPath =
                i == _subTreeRangePartitionCount - 1
                ? Stem.MaxValue
                : new Stem(Stem.Zero.Bytes.ToArray()) { Bytes = { [0] = curStartingPath } };

            partition.SubTreePathLimit = limitPath.Bytes;

            SubTreeRangePartitions[limitPath.Bytes] = partition;
            SubTreeRangeReadyForRequest.Enqueue(partition);
        }
    }

    public bool CanSync()
    {
        BlockHeader? header = _pivot.GetPivotHeader();
        if (header is null || header.Number == 0)
        {
            if (_logger.IsInfo) _logger.Info($"No Best Suggested Header available. Verkle Sync not started.");

            return false;
        }

        if (_logger.IsInfo) _logger.Info($"Starting the VERKLE_SYNC data sync from the {header.ToString(BlockHeader.Format.Short)} {header.StateRoot} root");

        return true;
    }

    public void UpdatePivot()
    {
        _pivot.UpdateHeaderForcefully();
    }

    public (VerkleSyncBatch request, bool finished) GetNextRequest()
    {
        Interlocked.Increment(ref _reqCount);

        var pivotHeader = _pivot.GetPivotHeader();
        var rootHash = pivotHeader.StateRoot;
        var blockNumber = pivotHeader.Number;

        VerkleSyncBatch request = new();

        if (!LeafsToRefresh.IsEmpty)
        {
            Interlocked.Increment(ref _activeLeafRefreshRequests);

            LogRequest($"LeafsToRefresh:{LeafsToRefresh.Count}");

            int queueLength = LeafsToRefresh.Count;
            byte[][] paths = new  byte[queueLength][];

            for (int i = 0; i < queueLength && LeafsToRefresh.TryDequeue(out var acc); i++)
            {
                paths[i] = acc;
            }

            request.LeafToRefreshRequest = new LeafToRefreshRequest() { RootHash = rootHash.Bytes.ToArray(), Paths = paths };

            return (request, false);

        }

        if (ShouldRequestSubTreeRequests() && SubTreeRangeReadyForRequest.TryDequeue(out SubTreeRangePartition partition))
        {
            Interlocked.Increment(ref _activeSubTreeRequests);

            SubTreeRange range = new(
                rootHash.Bytes.ToArray(),
                partition.NextSubTreePath.Bytes,
                partition.SubTreePathLimit.Bytes,
                blockNumber);

            LogRequest("SubTreeRange");

            request.SubTreeRangeRequest = range;

            return (request, false);
        }

        bool rangePhaseFinished = IsGetRangesFinished();
        if (rangePhaseFinished)
        {
            _logger.Info($"VERKLE_SYNC - State Ranges (Phase 1) finished.");
            FinishRangePhase();
        }

        LogRequest(NO_REQUEST);

        return (null, IsGetRangesFinished());
    }

    private bool ShouldRequestSubTreeRequests()
    {
        return _activeSubTreeRequests < _subTreeRangePartitionCount;
    }


    public void ReportLeafRefreshFinished(LeafToRefreshRequest leafToRefreshRequest = null)
    {
        if (leafToRefreshRequest is not null)
        {
            foreach (byte[]? path in leafToRefreshRequest.Paths)
            {
                LeafsToRefresh.Enqueue(path);
            }
        }

        Interlocked.Decrement(ref _activeLeafRefreshRequests);
    }

    public void EnqueueLeafRefresh(byte[] leaf)
    {
        LeafsToRefresh.Enqueue(leaf);
    }

    public void ReportSubTreeRangePartitionFinished(Stem hashLimit)
    {
        SubTreeRangePartition partition = SubTreeRangePartitions[hashLimit];

        if (partition.MoreSubTreesToRight)
        {
            SubTreeRangeReadyForRequest.Enqueue(partition);
        }
        Interlocked.Decrement(ref _activeSubTreeRequests);
    }

    public void UpdateSubTreePartitionProgress(Stem hashLimit, Stem nextPath, bool moreChildrenToRight)
    {
        SubTreeRangePartition partition = SubTreeRangePartitions[hashLimit];

        partition.NextSubTreePath = nextPath;
        partition.MoreSubTreesToRight = moreChildrenToRight && nextPath < hashLimit;
    }

    public bool IsGetRangesFinished()
    {
        return SubTreeRangeReadyForRequest.IsEmpty
               && LeafsToRefresh.IsEmpty
               && _activeSubTreeRequests == 0
               && _activeLeafRefreshRequests == 0;
    }

    private void GetSyncProgress()
    {
        // Note, as before, the progress actually only store MaxValue or 0. So we can't actually resume
        // verkle sync on restart.
        byte[] progress = _db.Get(SYNC_PROGRESS_KEY);
        if (progress is { Length: 31 })
        {
            Stem path = new(progress);

            if (path == Stem.MaxValue)
            {
                _logger.Info($"VERKLE_SYNC - State Ranges (Phase 1) is finished.");
                foreach (KeyValuePair<Stem, SubTreeRangePartition> partition in SubTreeRangePartitions)
                {
                    partition.Value.MoreSubTreesToRight = false;
                }
                SubTreeRangeReadyForRequest.Clear();
            }
            else
            {
                _logger.Info($"VERKLE_SYNC - State Ranges (Phase 1) progress loaded from DB:{path}");
            }
        }
    }

    private void FinishRangePhase()
    {
        _db.Set(SYNC_PROGRESS_KEY, Stem.MaxValue.Bytes);
    }

    private void LogRequest(string reqType)
    {
        if (_reqCount % 100 == 0)
        {
            int totalPathProgress = 0;
            foreach (KeyValuePair<Stem, SubTreeRangePartition> kv in SubTreeRangePartitions)
            {
                SubTreeRangePartition? partition = kv.Value;
                int nextAccount = partition.NextSubTreePath.Bytes[0] * 256 + partition.NextSubTreePath.Bytes[1];
                int startAccount = partition.SubTreePathStart.Bytes[0] * 256 + partition.SubTreePathStart.Bytes[1];
                totalPathProgress += nextAccount - startAccount;
            }

            double progress = 100 * totalPathProgress / (double)(256 * 256);

            if (_logger.IsInfo) _logger.Info($"VERKLE_SYNC - progress of State Ranges (Phase 1): {progress:f3}% [{new string('*', (int)progress / 10)}{new string(' ', 10 - (int)progress / 10)}]");
        }

        if (_logger.IsTrace || _reqCount % 1000 == 0)
        {
            int moreAccountCount = SubTreeRangePartitions.Count(kv => kv.Value.MoreSubTreesToRight);

            _logger.Info(
                $"VERKLE_SYNC - ({reqType}, diff:{_pivot.Diff}) {moreAccountCount} - Requests Account:{_activeSubTreeRequests} | Refresh:{_activeLeafRefreshRequests} -  Refresh:{LeafsToRefresh.Count}");
        }
    }


    // A partition of the top level account range starting from `SubTreePathStart` to `SubTreePathLimit` (exclusive).
    private class SubTreeRangePartition
    {
        public Stem NextSubTreePath { get; set; } = Stem.Zero;
        public Stem SubTreePathStart { get; set; } = Stem.Zero; // Not really needed, but useful
        public Stem SubTreePathLimit { get; set; } = Stem.MaxValue;
        public bool MoreSubTreesToRight { get; set; } = true;
    }
}
