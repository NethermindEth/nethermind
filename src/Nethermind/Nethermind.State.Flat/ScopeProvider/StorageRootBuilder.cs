// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Applies committed storage writes into the scope's storage tries on a dedicated thread while the block is still
/// executing, so the block-end flush only has to commit already-built (and mostly hashed) tries.
/// </summary>
/// <remarks>
/// Flat execution never reads the storage tries, and routing every committed write through the builder keeps the trie
/// warmer off those tries too, so this thread is the sole mutator of each tree until <see cref="CompleteAndJoin"/>
/// returns. Writes are applied in commit order, so a tree ends in the state the flush would have produced. A fault
/// poisons the builder; the scope then discards its tries and the flush rebuilds them from the committed parent, so
/// the concurrency never costs correctness.
/// </remarks>
internal sealed class StorageRootBuilder
{
    private readonly record struct Delta(FlatStorageTree Tree, UInt256 Index, UInt256 Value);

    private readonly BlockingCollection<Delta> _pending = new(new ConcurrentQueue<Delta>());
    private readonly HashSet<FlatStorageTree> _touched = [];
    private readonly Thread _thread;
    private readonly bool _eagerHash;
    private readonly ILogger _logger;
    private volatile bool _faulted;
    private volatile bool _drained;

    public StorageRootBuilder(bool eagerHash, ILogManager logManager)
    {
        _eagerHash = eagerHash;
        _logger = logManager.GetClassLogger<StorageRootBuilder>();
        _thread = new Thread(Run) { IsBackground = true, Name = nameof(StorageRootBuilder) };
        _thread.Start();
    }

    public bool IsFaulted => _faulted;

    /// <summary>True once every write has been applied and the thread has exited; the tries then belong to the caller.</summary>
    public bool IsDrained => _drained;

    public bool TryEnqueue(FlatStorageTree tree, in UInt256 index, in UInt256 value)
    {
        if (_faulted || _pending.IsAddingCompleted) return false;
        _pending.Add(new Delta(tree, index, value));
        return true;
    }

    public void CompleteAndJoin()
    {
        if (_drained) return;
        _pending.CompleteAdding();
        _thread.Join();
        _pending.Dispose();
        _drained = true;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                if (!_pending.TryTake(out Delta delta))
                {
                    // Nothing pending: the writes seen so far are final for now, so hash them before blocking.
                    if (_eagerHash) HashTouched();
                    if (!_pending.TryTake(out delta, Timeout.Infinite)) return;
                }

                delta.Tree.ApplyCommitted(delta.Index, delta.Value);
                if (_eagerHash) _touched.Add(delta.Tree);
            }
        }
        catch (Exception e)
        {
            _faulted = true;
            if (_logger.IsError) _logger.Error("Storage root builder faulted; storage tries will be rebuilt at commit.", e);
        }
    }

    private void HashTouched()
    {
        foreach (FlatStorageTree tree in _touched) tree.HashDirtyPaths();
        _touched.Clear();
    }
}
