// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>Prepares storage updates on a private trie without publishing nodes or hashing roots.</summary>
internal sealed class BackgroundStorageTrie(StorageTree tree, ConcurrencyController concurrency) : IWorldStateScopeProvider.IStorageWriteBatch
{
    internal const int MinimumBatchSize = 128;
    internal const int MaximumTrackedSlots = 65_536;

    private readonly Lock _lock = new();
    private readonly Dictionary<UInt256, UInt256> _values = [];
    private Dictionary<UInt256, UInt256> _pending = [];
    private Dictionary<UInt256, UInt256> _spare = [];
    private Task? _worker;
    private ExceptionDispatchInfo? _failure;
    private bool _running;
    private bool _stopped;
    private bool _discard;

    internal StorageTree Tree => tree;
    internal Task? Worker => _worker;
    internal bool IsStopped => _stopped;

    public void Set(in UInt256 index, in UInt256 value)
    {
        lock (_lock)
        {
            if (_stopped || _discard || _failure is not null) return;
            if (_values.Count == MaximumTrackedSlots && !_values.ContainsKey(index))
            {
                _discard = true;
                _pending.Clear();
                return;
            }

            _values[index] = value;
            _pending[index] = value;
            if (!_running && _pending.Count >= MinimumBatchSize && concurrency.TryRequestConcurrencyQuota())
            {
                _running = true;
                try
                {
                    _worker = Task.Run(Run);
                }
                catch
                {
                    _running = false;
                    concurrency.ReturnConcurrencyQuota();
                    throw;
                }
            }
        }
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                Dictionary<UInt256, UInt256> batch;
                lock (_lock)
                {
                    if (_stopped || _discard || _pending.Count < MinimumBatchSize) return;
                    batch = _pending;
                    _pending = _spare;
                }

                Apply(batch);
                batch.Clear();
                lock (_lock) _spare = batch;
            }
        }
        catch (Exception exception)
        {
            lock (_lock) _failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            lock (_lock)
            {
                concurrency.ReturnConcurrencyQuota();
                _running = false;
            }
        }
    }

    private void Apply(Dictionary<UInt256, UInt256> batch)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(batch.Count);
        ValueHash256 key = default;
        foreach ((UInt256 index, UInt256 value) in batch)
        {
            StorageTree.ComputeKeyWithLookup(index, ref key);
            entries.Add(new PatriciaTree.BulkSetEntry(key, value.IsZero ? [] : Rlp.Encode(value).Bytes));
        }

        // Contracts share a bounded worker budget; nested trie parallelism would compete with execution.
        tree.BulkSet(entries, PatriciaTree.Flags.DoNotParallelize);
    }

    internal bool Complete()
    {
        Task? worker;
        lock (_lock)
        {
            if (_stopped) return false;
            _stopped = true;
            worker = _worker;
        }

        worker?.GetAwaiter().GetResult();
        ThrowFailure();
        if (_discard || worker is null) return false;
        Apply(_pending);
        _pending.Clear();
        return true;
    }

    internal bool Contains(in UInt256 index, in UInt256 value) =>
        _values.TryGetValue(index, out UInt256 prepared) && prepared == value;

    public void Clear() => Dispose();

    public void Dispose()
    {
        Task? worker;
        lock (_lock)
        {
            _stopped = true;
            worker = _worker;
        }

        worker?.GetAwaiter().GetResult();
        _pending.Clear();
        _spare.Clear();
        _values.Clear();
        ThrowFailure();
    }

    private void ThrowFailure()
    {
        ExceptionDispatchInfo? failure = _failure;
        _failure = null;
        failure?.Throw();
    }
}
