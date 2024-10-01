// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

public interface IState : IReadOnlyState
{
    void Set(Address address, in Account account, bool isNewHint = false);

    void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value);

    /// <summary>
    /// Informs the state about the potential of this sell being set.
    /// Might be used for prefetching purposes of the commitment.
    /// </summary>
    void StorageMightBeSet(in StorageCell cell);

    /// <summary>
    /// Commits the changes.
    /// </summary>
    void Commit(long blockNumber);

    /// <summary>
    /// Resets all the changes.
    /// </summary>
    void Reset();
}

public interface IReadOnlyState : IDisposable
{
    bool TryGet(Address address, out Account account);

    /// <summary>
    /// Gets storage by the cell.
    /// </summary>
    ReadOnlySpan<byte> GetStorageAt(scoped in StorageCell cell);

    /// <summary>
    /// Gets storage by the index that has already been hashed.
    /// </summary>
    ReadOnlySpan<byte> GetStorageAt(Address address, in ValueHash256 hash);

    Hash256 StateRoot { get; }
}

/// <summary>
/// The factory allowing to get a state at the given keccak.
/// </summary>
public interface IStateFactory : IAsyncDisposable
{
    IState Get(Hash256 stateRoot);

    IReadOnlyState GetReadOnly(Hash256? stateRoot);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    bool HasRoot(Hash256 stateRoot);

    public bool TryGet(Hash256 stateRoot, Address address, out AccountStruct account);

    public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, scoped in Address address, in UInt256 index);
}

public interface IStateOwner
{
    IState State { get; }
}
