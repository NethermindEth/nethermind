// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie.Pruning;
using Paprika.Chain;
using Paprika.Data;
using EvmWord = System.Runtime.Intrinsics.Vector256<byte>;

namespace Nethermind.State;

public interface IState : IReadOnlyState
{
    void Set(Address address, in AccountStruct account, bool isNewHint = false);

    void SetStorage(in StorageCell cell, EvmWord value);

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
    Account? Get(ValueHash256 hash);
    bool TryGet(Address address, out AccountStruct account);

    /// <summary>
    /// Gets storage by the cell.
    /// </summary>
    EvmWord GetStorageAt(in StorageCell cell);

    /// <summary>
    /// Gets storage by the index that has already been hashed.
    /// </summary>
    EvmWord GetStorageAt(Address address, in ValueHash256 hash);

    EvmWord GetStorageAt(in ValueHash256 accountHash, in ValueHash256 hash);

    Hash256 StateRoot { get; }
}

public interface IRawState : IReadOnlyState
{
    void SetAccount(ValueHash256 hash, Account? account);
    void SetAccountHash(ReadOnlySpan<byte> keyPath, int targetKeyLength, Hash256 keccak);
    void SetStorage(in StorageCell cell, EvmWord value);
    void SetStorage(ValueHash256 accountHash, ValueHash256 storageSlotHash, ReadOnlySpan<byte> encodedValue);
    void SetStorageHash(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength, Hash256 keccak);
    void Commit(bool ensureHash);
    ValueHash256 GetHash(ReadOnlySpan<byte> path, int pathLength, bool ignoreCache);
    ValueHash256 GetStorageHash(ValueHash256 accountHash, ReadOnlySpan<byte> storagePath, int pathLength);
    void RemoveBoundaryProof(ValueHash256 accountHash, ReadOnlySpan<byte> storagePath, int pathLength);
    void RemoveBoundaryProof(ReadOnlySpan<byte> path, int pathLength);

    void CreateProofBranch(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength,
        byte[] childNibbles, Hash256[] childHashes, bool persist = true);

    void CreateProofExtension(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength,
        int extPathLength, bool persist = true);

    void CreateProofLeaf(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength, int leafKeyIndex);

    void Finalize(uint blockNumber);
    string DumpTrie();
    ValueHash256 RefreshRootHash();
    ValueHash256 RecalculateStorageRoot(ValueHash256 accountHash);
    public void Discard();
}

/// <summary>
/// The factory allowing to get a state at the given keccak.
/// </summary>
public interface IStateFactory : IAsyncDisposable
{
    IState Get(Hash256 stateRoot);

    IReadOnlyState GetReadOnly(Hash256? stateRoot);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public IRawState GetRaw();
    public IRawState GetRaw(ValueHash256 rootHash);

    bool HasRoot(Hash256 stateRoot);

    bool TryGet(Hash256 stateRoot, Address address, out AccountStruct account);

    EvmWord GetStorage(Hash256 stateRoot, in Address address, in UInt256 index);

    void ForceFlush();

    void ResetAccessor();
}

public interface IStateOwner
{
    IState State { get; }
}
