// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Applies committed storage writes into the scope's storage tries on dedicated threads while the block is still
/// executing, so the block-end flush only has to commit already-built (and mostly hashed) tries.
/// </summary>
/// <remarks>
/// Warm threads first resolve each write's path nodes into the node cache, then the tree's shard thread (chosen by
/// address) applies it, so the Set finds warm nodes. Flat execution never reads the storage tries, so each tree has a
/// single mutator until
/// <see cref="CompleteAndJoin"/> returns. Writes are applied in commit order, so a tree ends in the state the flush would
/// have produced. A fault poisons the builder; the scope then discards its tries and the flush rebuilds them from the
/// committed parent, so the concurrency never costs correctness.
/// </remarks>
internal sealed class StorageRootBuilder
{
    private sealed class Delta(FlatStorageTree tree, in UInt256 index, in UInt256 value)
    {
        public readonly FlatStorageTree Tree = tree;
        public readonly UInt256 Index = index;
        public readonly UInt256 Value = value;
        public volatile bool Warmed;
    }

    private sealed class Shard
    {
        public readonly BlockingCollection<Delta> Pending = new(new ConcurrentQueue<Delta>());
        public readonly HashSet<FlatStorageTree> Touched = [];
        public Thread Thread = null!;
    }

    private readonly BlockingCollection<Delta> _toWarm = new(new ConcurrentQueue<Delta>());
    private readonly Thread[] _warmThreads;
    private readonly Shard[] _shards;
    private readonly bool _eagerHash;
    private readonly ILogger _logger;
    private volatile bool _faulted;
    private volatile bool _drained;

    public StorageRootBuilder(int threads, bool eagerHash, ILogManager logManager)
    {
        _eagerHash = eagerHash;
        _logger = logManager.GetClassLogger<StorageRootBuilder>();
        threads = Math.Max(1, threads);
        _shards = new Shard[threads];
        for (int i = 0; i < threads; i++)
        {
            Shard shard = new();
            shard.Thread = new Thread(() => Apply(shard)) { IsBackground = true, Name = $"{nameof(StorageRootBuilder)}-apply-{i}" };
            _shards[i] = shard;
            shard.Thread.Start();
        }

        _warmThreads = new Thread[threads];
        for (int i = 0; i < threads; i++)
        {
            _warmThreads[i] = new Thread(Warm) { IsBackground = true, Name = $"{nameof(StorageRootBuilder)}-warm-{i}" };
            _warmThreads[i].Start();
        }
    }

    public bool IsFaulted => _faulted;

    /// <summary>True once every write has been applied and the threads have exited; the tries then belong to the caller.</summary>
    public bool IsDrained => _drained;

    public bool TryEnqueue(FlatStorageTree tree, in UInt256 index, in UInt256 value)
    {
        if (_faulted || _drained) return false;
        Delta delta = new(tree, in index, in value);
        try
        {
            // Warm first so the apply thread never sees a delta that no warm thread will ever mark.
            _toWarm.Add(delta);
            _shards[(int)((uint)tree.BuilderShardKey % (uint)_shards.Length)].Pending.Add(delta);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Adding completed concurrently; the caller falls back to the serial path for this write.
            delta.Warmed = true;
            return false;
        }
    }

    public void CompleteAndJoin()
    {
        if (_drained) return;
        long start = Stopwatch.GetTimestamp();
        _toWarm.CompleteAdding();
        foreach (Shard shard in _shards)
        {
            Db.Metrics.ParallelStorageRootDrainBacklog += shard.Pending.Count;
            shard.Pending.CompleteAdding();
        }
        foreach (Thread thread in _warmThreads) thread.Join();
        foreach (Shard shard in _shards) shard.Thread.Join();
        Db.Metrics.ParallelStorageRootDrainWaitMicros += (long)Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        _drained = true;
        _toWarm.Dispose();
        foreach (Shard shard in _shards) shard.Pending.Dispose();
    }

    private void Warm()
    {
        while (_toWarm.TryTake(out Delta? delta, Timeout.Infinite))
        {
            try
            {
                if (!_faulted) delta.Tree.WarmPathForBuilder(in delta.Index);
            }
            catch (Exception e)
            {
                Fault(e);
            }
            finally
            {
                delta.Warmed = true;
            }
        }
    }

    private void Apply(Shard shard)
    {
        try
        {
            while (true)
            {
                if (!shard.Pending.TryTake(out Delta? delta))
                {
                    // Nothing pending: the writes seen so far are final for now, so hash them before blocking.
                    if (_eagerHash) HashTouched(shard);
                    if (!shard.Pending.TryTake(out delta, Timeout.Infinite)) return;
                }

                SpinWait spinner = new();
                while (!delta.Warmed) spinner.SpinOnce(sleep1Threshold: -1);

                long start = Stopwatch.GetTimestamp();
                delta.Tree.ApplyCommitted(delta.Index, delta.Value);
                long micros = (long)Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                Db.Metrics.AddParallelStorageRootApplyMicros(micros);
                if (shard.Pending.IsAddingCompleted) Db.Metrics.AddParallelStorageRootTailApplyMicros(micros);
                if (_eagerHash) shard.Touched.Add(delta.Tree);
            }
        }
        catch (Exception e)
        {
            Fault(e);
        }
    }

    private void Fault(Exception e)
    {
        _faulted = true;
        if (_logger.IsError) _logger.Error("Storage root builder faulted; storage tries will be rebuilt at commit.", e);
    }

    private static void HashTouched(Shard shard)
    {
        foreach (FlatStorageTree tree in shard.Touched)
        {
            // Once the block thread is waiting for the join, the parallel flush hashes what is left faster than this thread.
            if (shard.Pending.IsAddingCompleted) break;
            long start = Stopwatch.GetTimestamp();
            tree.HashDirtyPaths();
            long micros = (long)Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            Db.Metrics.AddParallelStorageRootHashMicros(micros);
            if (shard.Pending.IsAddingCompleted) Db.Metrics.AddParallelStorageRootTailHashMicros(micros);
        }
        shard.Touched.Clear();
    }
}
