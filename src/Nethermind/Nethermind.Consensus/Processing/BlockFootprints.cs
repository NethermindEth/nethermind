// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>The footprints of one block's warm runs, by transaction index.</summary>
internal sealed class BlockFootprints(Block block)
{
    private readonly Hash256? _blockHash = block.Hash;
    private readonly TransactionFootprint?[] _footprints = new TransactionFootprint?[block.Transactions.Length];

    // Experiment only: what each transaction's run came to, and when the warm-up started and ended.
    private readonly int[] _status = new int[block.Transactions.Length];
    public readonly long CreatedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    public long WarmedAt;

    public void SetStatus(int index, int status) => Volatile.Write(ref _status[index], status);

    public int Count => _footprints.Length;

    public TransactionFootprint? Get(int index) => Volatile.Read(ref _footprints[index]);

    public int Status(int index, Transaction tx)
    {
        if ((uint)index >= (uint)_status.Length) return HandoffDiagnostics.OtherTransaction;
        TransactionFootprint? footprint = Volatile.Read(ref _footprints[index]);
        return footprint is not null && !ReferenceEquals(footprint.Transaction, tx) ? HandoffDiagnostics.OtherTransaction : Volatile.Read(ref _status[index]);
    }

    /// <remarks>
    /// Needs receipts without a per-transaction state root (EIP-658). EIP-8037 gas accounting and a block access list
    /// (EIP-7928) are built while transactions execute, which a replay does not.
    /// </remarks>
    public static bool AppliesTo(Block block, IReleaseSpec spec) =>
        block.Transactions.Length > 0
        && spec.IsEip658Enabled
        && !spec.IsEip8037Enabled
        && !spec.BlockLevelAccessListsEnabled
        && block.BlockAccessList is null;

    /// <remarks>
    /// A warm run skips the pre-execution checks; the nonce is checked when it is recorded and the rest when it is
    /// replayed, except a nonce that would overflow (EIP-2681) and a zero fee, which are excluded here.
    /// </remarks>
    public static bool IsRecordable(Transaction tx) =>
        tx.SenderAddress is not null
        && !tx.SupportsFrames
        && !tx.IsSystem()
        && tx.Nonce != ulong.MaxValue
        && !(tx.MaxFeePerGas.IsZero && tx.MaxPriorityFeePerGas.IsZero);

    public void Store(int index, TransactionFootprint footprint)
    {
        Volatile.Write(ref _footprints[index], footprint);
        if (RewarmCounters.Enabled) Track(index, footprint);
    }

    // Experiment only: the block's footprint storage writes by position, and the positions whose footprint read each
    // slot, so a footprint that earlier positions' writes invalidate can be warmed again on the values they leave.
    private readonly Lock _versionsLock = new();
    private readonly Dictionary<StorageCell, List<(int Position, UInt256 Value)>> _slotVersions = [];
    private readonly Dictionary<StorageCell, List<(int Position, UInt256 Value)>> _slotReaders = [];
    // A slot more positions touch than this is left out: tracking it would cost more than warming its readers again saves.
    private const int MaxTrackedPerSlot = 64;
    private const int MaxRewarmsPerBlock = 128;
    private readonly HashSet<StorageCell> _hotSlots = [];
    private int _rewarmsTaken;
    private readonly Dictionary<int, (StorageCell[] Written, StorageCell[] Read)> _keysByPosition = [];
    private readonly SortedSet<int> _stale = [];
    private readonly SemaphoreSlim _staleSignal = new(0);
    private volatile bool _warmPassEnded;

    private void Track(int position, TransactionFootprint footprint)
    {
        Dictionary<StorageCell, UInt256> writes = [];
        foreach (ref readonly StateEffect effect in footprint.Effects)
        {
            if (effect.Kind == EffectKind.SetStorage) writes[new StorageCell(effect.Address, effect.Index)] = effect.Value;
        }

        ReadOnlySpan<SlotPrecondition> reads = footprint.Slots;
        bool marked = false;
        lock (_versionsLock)
        {
            if (_keysByPosition.Remove(position, out (StorageCell[] Written, StorageCell[] Read) replaced))
            {
                foreach (StorageCell cell in replaced.Written)
                {
                    if (_slotVersions.TryGetValue(cell, out List<(int Position, UInt256 Value)>? versions)) versions.RemoveAll(v => v.Position == position);
                }

                foreach (StorageCell cell in replaced.Read)
                {
                    if (_slotReaders.TryGetValue(cell, out List<(int Position, UInt256 Value)>? readers)) readers.RemoveAll(r => r.Position == position);
                }
            }

            StorageCell[] written = new StorageCell[writes.Count];
            int w = 0;
            foreach ((StorageCell cell, UInt256 value) in writes)
            {
                if (_hotSlots.Contains(cell)) continue;
                ref List<(int Position, UInt256 Value)>? versions = ref CollectionsMarshal.GetValueRefOrAddDefault(_slotVersions, cell, out _);
                if ((versions ??= []).Count >= MaxTrackedPerSlot)
                {
                    MakeHot(cell);
                    continue;
                }

                written[w++] = cell;
                InsertVersion(versions, position, value);
            }

            if (w < written.Length) Array.Resize(ref written, w);

            foreach (StorageCell cell in written) marked |= MarkReaders(cell, position);
            if (replaced.Written is not null)
            {
                foreach (StorageCell cell in replaced.Written) marked |= MarkReaders(cell, position);
            }

            List<StorageCell> read = new(reads.Length);
            bool stale = false;
            for (int i = 0; i < reads.Length; i++)
            {
                StorageCell cell = reads[i].Cell;
                if (_hotSlots.Contains(cell)) continue;
                ref List<(int Position, UInt256 Value)>? readers = ref CollectionsMarshal.GetValueRefOrAddDefault(_slotReaders, cell, out _);
                if ((readers ??= []).Count >= MaxTrackedPerSlot)
                {
                    MakeHot(cell);
                    continue;
                }

                read.Add(cell);
                readers.Add((position, reads[i].Value));
                if (VersionBefore(cell, position) is { } before && before != reads[i].Value) stale = true;
            }

            _keysByPosition[position] = (written, [.. read]);
            if (stale && _stale.Add(position))
            {
                marked = true;
                Interlocked.Increment(ref RewarmCounters.Marked);
            }
        }

        if (marked) _staleSignal.Release();
    }

    // Marks the later readers of the slot whose footprint read a value other than the one the block now leaves it at.
    private bool MarkReaders(StorageCell cell, int position)
    {
        if (!_slotReaders.TryGetValue(cell, out List<(int Position, UInt256 Value)>? readers)) return false;
        bool marked = false;
        foreach ((int reader, UInt256 read) in readers)
        {
            if (reader <= position || VersionBefore(cell, reader) is not { } value || read == value) continue;
            if (_stale.Add(reader))
            {
                marked = true;
                Interlocked.Increment(ref RewarmCounters.Marked);
            }
        }

        return marked;
    }

    // Stops tracking the slot; its readers are no longer warmed again for it.
    private void MakeHot(StorageCell cell)
    {
        _hotSlots.Add(cell);
        _slotVersions.Remove(cell);
        _slotReaders.Remove(cell);
    }

    /// <summary>Whether the footprint read a slot too many positions touch to be tracked.</summary>
    public bool ReadsHotSlot(TransactionFootprint footprint)
    {
        lock (_versionsLock)
        {
            if (_hotSlots.Count == 0) return false;
            foreach (ref readonly SlotPrecondition slot in footprint.Slots)
            {
                if (_hotSlots.Contains(slot.Cell)) return true;
            }

            return false;
        }
    }

    private UInt256? VersionBefore(in StorageCell cell, int position)
    {
        if (!_slotVersions.TryGetValue(cell, out List<(int Position, UInt256 Value)>? versions)) return null;
        int at = FirstAtOrAfter(versions, position);
        return at == 0 ? null : versions[at - 1].Value;
    }

    private static int FirstAtOrAfter(List<(int Position, UInt256 Value)> versions, int position)
    {
        int low = 0, high = versions.Count;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (versions[mid].Position < position) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    private static void InsertVersion(List<(int Position, UInt256 Value)> versions, int position, in UInt256 value) =>
        versions.Insert(FirstAtOrAfter(versions, position), (position, value));

    /// <summary>The value the block's earlier footprints leave <paramref name="cell"/> at before <paramref name="position"/>.</summary>
    public UInt256? SlotBefore(in StorageCell cell, int position)
    {
        lock (_versionsLock) return VersionBefore(cell, position);
    }

    /// <summary>Takes the first stale position after <paramref name="after"/>, dropping the ones before it.</summary>
    public bool TryTakeStale(int after, out int position)
    {
        lock (_versionsLock)
        {
            while (_stale.Count > 0 && _rewarmsTaken < MaxRewarmsPerBlock)
            {
                int first = _stale.Min;
                _stale.Remove(first);
                if (first > after)
                {
                    _rewarmsTaken++;
                    position = first;
                    return true;
                }
            }
        }

        position = -1;
        return false;
    }

    public bool HasStale
    {
        get
        {
            lock (_versionsLock) return _stale.Count > 0 && _rewarmsTaken < MaxRewarmsPerBlock;
        }
    }

    public bool WarmPassEnded => _warmPassEnded;

    public void EndWarmPass()
    {
        _warmPassEnded = true;
        _staleSignal.Release();
    }

    public void WaitForStale(TimeSpan timeout, CancellationToken token)
    {
        try
        {
            _staleSignal.Wait(timeout, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The footprint of <paramref name="tx"/>, at <paramref name="index"/> in the block <paramref name="header"/> heads.</summary>
    public TransactionFootprint? Find(int index, Transaction tx, BlockHeader header)
    {
        TransactionFootprint?[] footprints = _footprints;
        if ((uint)index >= (uint)footprints.Length || _blockHash is null || header.Hash != _blockHash) return null;
        TransactionFootprint? footprint = Volatile.Read(ref footprints[index]);
        return footprint is not null && ReferenceEquals(footprint.Transaction, tx) ? footprint : null;
    }
}
