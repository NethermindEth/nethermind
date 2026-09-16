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
/// Flat execution never reads the storage tries, and routing every committed write through the builder keeps the trie
/// warmer off those tries too, so each tree has a single mutator (its shard thread, chosen by address) until
/// <see cref="CompleteAndJoin"/> returns. Writes are applied in commit order, so a tree ends in the state the flush would
/// have produced. A fault poisons the builder; the scope then discards its tries and the flush rebuilds them from the
/// committed parent, so the concurrency never costs correctness.
/// </remarks>
internal sealed class StorageRootBuilder
{
    private readonly record struct Delta(FlatStorageTree Tree, UInt256 Index, UInt256 Value);

    private sealed class Shard
    {
        public readonly BlockingCollection<Delta> Pending = new(new ConcurrentQueue<Delta>());
        public readonly HashSet<FlatStorageTree> Touched = [];
        public Thread Thread = null!;
    }

    private readonly Shard[] _shards;
    private readonly bool _eagerHash;
    private readonly ILogger _logger;
    private volatile bool _faulted;
    private volatile bool _drained;

    public StorageRootBuilder(int threads, bool eagerHash, ILogManager logManager)
    {
        _eagerHash = eagerHash;
        _logger = logManager.GetClassLogger<StorageRootBuilder>();
        _shards = new Shard[Math.Max(1, threads)];
        for (int i = 0; i < _shards.Length; i++)
        {
            Shard shard = new();
            shard.Thread = new Thread(() => Run(shard)) { IsBackground = true, Name = $"{nameof(StorageRootBuilder)}-{i}" };
            _shards[i] = shard;
            shard.Thread.Start();
        }
    }

    public bool IsFaulted => _faulted;

    /// <summary>True once every write has been applied and the threads have exited; the tries then belong to the caller.</summary>
    public bool IsDrained => _drained;

    public bool TryEnqueue(FlatStorageTree tree, in UInt256 index, in UInt256 value)
    {
        if (_faulted || _drained) return false;
        try
        {
            _shards[(int)((uint)tree.BuilderShardKey % (uint)_shards.Length)].Pending.Add(new Delta(tree, index, value));
            return true;
        }
        catch (InvalidOperationException)
        {
            // Adding completed concurrently; the caller falls back to the serial path for this write.
            return false;
        }
    }

    public void CompleteAndJoin()
    {
        if (_drained) return;
        long start = Stopwatch.GetTimestamp();
        foreach (Shard shard in _shards)
        {
            Db.Metrics.ParallelStorageRootDrainBacklog += shard.Pending.Count;
            shard.Pending.CompleteAdding();
        }
        foreach (Shard shard in _shards) shard.Thread.Join();
        Db.Metrics.ParallelStorageRootDrainWaitMicros += (long)Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        _drained = true;
        foreach (Shard shard in _shards) shard.Pending.Dispose();
    }

    private void Run(Shard shard)
    {
        try
        {
            while (true)
            {
                if (!shard.Pending.TryTake(out Delta delta))
                {
                    // Nothing pending: the writes seen so far are final for now, so hash them before blocking.
                    if (_eagerHash) HashTouched(shard);
                    if (!shard.Pending.TryTake(out delta, Timeout.Infinite)) return;
                }

                delta.Tree.ApplyCommitted(delta.Index, delta.Value);
                if (_eagerHash) shard.Touched.Add(delta.Tree);
            }
        }
        catch (Exception e)
        {
            _faulted = true;
            if (_logger.IsError) _logger.Error("Storage root builder faulted; storage tries will be rebuilt at commit.", e);
        }
    }

    private static void HashTouched(Shard shard)
    {
        foreach (FlatStorageTree tree in shard.Touched)
        {
            // Once the block thread is waiting for the join, the parallel flush hashes what is left faster than this thread.
            if (shard.Pending.IsAddingCompleted) break;
            tree.HashDirtyPaths();
        }
        shard.Touched.Clear();
    }
}
