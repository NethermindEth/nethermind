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
    void Set(Address address, Account? account, bool isNewHint = false);

    void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value);
    IStorage GetStorageSetter(in Address address);

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

public interface IStorage
{
    void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value);

    ValueHash256 Keccak { get; }
}

public interface IReadOnlyState : IDisposable
{
    bool TryGet(Address address, out AccountStruct account);

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

public interface IRawState : IReadOnlyState
{
    void SetAccount(ValueHash256 hash, Account? account);
    void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value);
    void SetStorage(ValueHash256 accountHash, ValueHash256 storageSlotHash, ReadOnlySpan<byte> encodedValue);
    void Commit(bool ensureHash);
    ValueHash256 GetHash(ReadOnlySpan<byte> path, int pathLength, bool ignoreCache);
    ValueHash256 GetStorageHash(ValueHash256 accountHash, ReadOnlySpan<byte> storagePath, int pathLength, bool ignoreCache);
    ValueHash256 RecalculateRootHash();

    bool IsPersisted(ValueHash256 accountHash, ReadOnlySpan<byte> path, int pathLength);

    void CreateProofBranch(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength,
        byte[] childNibbles, Hash256[] childHashes, bool persist = true);

    void CreateProofExtension(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength,
        int extPathLength, bool persist = true);

    void CreateProofLeaf(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength, int leafKeyIndex);

    void RegisterDeleteByPrefix(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength);

    void Finalize(uint blockNumber);
    string DumpTrie();
    ValueHash256 RefreshRootHash();
    ValueHash256 RecalculateStorageRoot(ValueHash256 accountHash);
    public void Discard();
    void ProcessProofNodes(ValueHash256 accountHash, Span<byte> packedProofPaths, int proofCount);
}

/// <summary>
/// The factory allowing to get a state at the given keccak.
/// </summary>
public interface IStateFactory : IAsyncDisposable
{
    IState Get(Hash256 stateRoot, bool prefetchMerkle);

    IReadOnlyState GetReadOnly(Hash256? stateRoot);

    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public IRawState GetRaw();
    public IRawState GetRaw(ValueHash256 rootHash);

    bool HasRoot(Hash256 stateRoot);

    public bool TryGet(Hash256 stateRoot, Address address, out AccountStruct account);

    public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, scoped in Address address, in UInt256 index);
    
    void ForceFlush();

    void ResetAccessor();
}

public interface IStateOwner
{
    IState State { get; }
}
