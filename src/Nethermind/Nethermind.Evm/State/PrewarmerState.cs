// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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
/// <remarks>Used by the block-processing thread only: it starts and ends the collection around a transaction it executes.</remarks>
internal sealed class CommittedStorageWrites
{
    private List<(StorageCell Cell, UInt256 Value)>? _writes;
    private bool _collecting;

    public void Begin()
    {
        _writes = null;
        _collecting = true;
    }

    /// <summary>Called for each slot a commit of the main world state changes.</summary>
    public void Add(Address address, in UInt256 index, in UInt256 value)
    {
        if (_collecting) (_writes ??= []).Add((new StorageCell(address, in index), value));
    }

    /// <returns>The writes collected since <see cref="Begin"/>; null when there were none.</returns>
    public List<(StorageCell Cell, UInt256 Value)>? End()
    {
        _collecting = false;
        List<(StorageCell Cell, UInt256 Value)>? writes = _writes;
        _writes = null;
        return writes;
    }
}
