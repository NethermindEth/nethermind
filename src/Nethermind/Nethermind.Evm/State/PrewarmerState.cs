// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

/// <summary>
/// The prewarmer state of a lifetime scope: the block caches shared with the main execution, and whether this
/// scope is a speculative populator or the main execution that consumes them.
/// </summary>
/// <remarks>
/// Registered once per lifetime scope so components that must behave differently in a speculative env can be
/// wired to the right value instead of probing the world state or its scope provider at run time.
/// </remarks>
public interface IPrewarmerState
{
    PreBlockCaches Caches { get; }

    /// <summary>True for read-only populator envs; false for the read-write main world state.</summary>
    bool IsPrewarmer { get; }

    /// <summary>The storage writes the main world state commits while block processing collects them; none for populators.</summary>
    internal CommittedStorageWrites? CommittedWrites => null;
}

/// <inheritdoc cref="IPrewarmerState"/>
public sealed class PrewarmerState(PreBlockCaches caches, bool isPrewarmer) : IPrewarmerState
{
    private readonly CommittedStorageWrites? _committedWrites = isPrewarmer ? null : new();

    public PreBlockCaches Caches => caches;
    public bool IsPrewarmer => isPrewarmer;
    CommittedStorageWrites? IPrewarmerState.CommittedWrites => _committedWrites;
}

/// <summary>
/// The slots a transaction's commit changes, at the values it leaves them, while block processing collects them.
/// </summary>
/// <remarks>
/// Used by the block-processing thread only: it starts and ends the collection around a transaction it executes.
/// The writes are appended to a shared chunk that a handed-out range is never written in again, so other threads can
/// read it; a transaction allocates only when it fills a chunk.
/// </remarks>
internal sealed class CommittedStorageWrites
{
    // Below the large object heap.
    private const int ChunkLength = 256;

    private (StorageCell Cell, UInt256 Value)[] _chunk = new (StorageCell, UInt256)[ChunkLength];
    private int _start;
    private int _used;
    private bool _collecting;

    public void Begin()
    {
        _start = _used;
        _collecting = true;
    }

    /// <summary>Called for each slot a commit of the main world state changes.</summary>
    public void Add(Address address, in UInt256 index, in UInt256 value)
    {
        if (!_collecting) return;
        (StorageCell Cell, UInt256 Value)[] chunk = _chunk;
        int used = _used;
        if ((uint)used < (uint)chunk.Length)
        {
            // Written in place: building the pair first adds a temporary the frame has to clear.
            ref (StorageCell Cell, UInt256 Value) entry = ref chunk[used];
            entry.Cell = new StorageCell(address, in index);
            entry.Value = value;
            _used = used + 1;
        }
        else
        {
            AddToNextChunk(address, in index, in value);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddToNextChunk(Address address, in UInt256 index, in UInt256 value)
    {
        NextChunk();
        _chunk[_used++] = (new StorageCell(address, in index), value);
    }

    /// <returns>The writes collected since <see cref="Begin"/>; empty when there were none.</returns>
    public ReadOnlyMemory<(StorageCell Cell, UInt256 Value)> End()
    {
        _collecting = false;
        return _chunk.AsMemory(_start, _used - _start);
    }

    // The transaction's writes so far move with it, so its range stays in one chunk.
    private void NextChunk()
    {
        int count = _used - _start;
        (StorageCell Cell, UInt256 Value)[] next = new (StorageCell, UInt256)[Math.Max(ChunkLength, count * 2)];
        Array.Copy(_chunk, _start, next, 0, count);
        _chunk = next;
        _start = 0;
        _used = count;
    }
}
