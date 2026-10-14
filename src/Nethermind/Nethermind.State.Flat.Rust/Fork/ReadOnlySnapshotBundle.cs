// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// A read-only bundle of the native snapshots of a state over a persistence reader: the native read-only bundle, or the
/// base view of a native scope, which reads the scope's bundle without its own snapshots and current content.
/// </summary>
/// <remarks>
/// It holds a reference on its handle until its last lease is released, so the native scope it was opened with is not
/// freed under a warmer leasing it after the <see cref="SnapshotBundle"/> that took the scope is disposed; the warmer's
/// next read then throws <see cref="ObjectDisposedException"/>. Trie nodes are never found as objects, only loaded as
/// RLP; every read records the metrics of where it was served from. The native scope reads of
/// <see cref="SnapshotBundle"/> go through the same helpers, with its scope handle.
/// </remarks>
public sealed class ReadOnlySnapshotBundle : RefCountingDisposable
{
    private const int StackBufferSize = 1024;
    private const int MaxStackSeedCount = 64;

    private static readonly StringLabel _readAccountSnapshotLabel = new("account_snapshot");
    private static readonly StringLabel _readAccountPersistenceLabel = new("account_persistence");
    private static readonly StringLabel _readAccountPersistenceNullLabel = new("account_persistence_null");
    private static readonly StringLabel _readStorageSnapshotLabel = new("storage_snapshot");
    private static readonly StringLabel _readStoragePersistenceLabel = new("storage_persistence");
    private static readonly StringLabel _readStoragePersistenceNullLabel = new("storage_persistence_null");
    private static readonly StringLabel _readStateNodeSnapshotLabel = new("state_node_snapshot");
    private static readonly StringLabel _readStorageNodeSnapshotLabel = new("storage_node_snapshot");
    private static readonly StringLabel _readStateRlpLabel = new("state_rlp");
    private static readonly StringLabel _readStorageRlpLabel = new("storage_rlp");
    private static readonly StringLabel _readAccountPersistedLabel = new("account_persisted_snapshot");
    private static readonly StringLabel _readStoragePersistedLabel = new("storage_persisted_snapshot");
    private static readonly StringLabel _readStateRlpPersistedLabel = new("state_rlp_persisted_snapshot");
    private static readonly StringLabel _readStorageRlpPersistedLabel = new("storage_rlp_persisted_snapshot");

    private readonly BundleBaseHandle _handle;
    private readonly bool _recordDetailedMetrics;
    private readonly bool _hasSelfDestructs;
    private int _scopeTaken;
    private bool _isDisposed;

    /// <param name="handle">The native bundle, or a native scope whose base view this is; the bundle disposes it,
    /// unless a <see cref="SnapshotBundle"/> takes the scope with <see cref="TakeOrCreateScope"/>.</param>
    /// <param name="info">What the native call opening <paramref name="handle"/> reported.</param>
    internal ReadOnlySnapshotBundle(BundleBaseHandle handle, in FfiRobInfo info, bool recordDetailedMetrics)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        _handle = handle;
        _recordDetailedMetrics = recordDetailedMetrics;
        _hasSelfDestructs = info.HasSelfDestructs != 0;
        SnapshotCount = (int)info.SnapshotCount;
        MayFilterSlots = info.MayFilterSlots != 0;
    }

    /// <summary>The snapshots of the bundle, which number its self-destruct indexes from 0.</summary>
    public int SnapshotCount { get; }

    /// <summary>Always <c>false</c>: the native backend serves no history.</summary>
    public bool IsHistorical => false;

    /// <summary><c>false</c> when <see cref="GetSlotFiltered"/> can only read as <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}}, out UInt256?)"/> does.</summary>
    internal bool MayFilterSlots { get; }

    public bool TryLease() => TryAcquireLease();

    public Account? GetAccount(Address address)
    {
        GuardDispose();
        return ReadAccount(null, promote: false, address);
    }

    public Account? GetAccount(Address address, HashedKey<Address> key) => GetAccount(address);

    public unsafe int DetermineSelfDestructSnapshotIdx(Address address)
    {
        if (!_hasSelfDestructs) return -1;

        int index;
        using BundleBaseHandle.Call call = _handle.Enter();
        fixed (byte* addressBytes = address.Bytes)
        {
            _handle.Store.ThrowIfFailed(NativeMethods.fdb_rob_self_destruct_idx(call.Bundle, addressBytes, &index));
        }

        return index;
    }

    public void GetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out UInt256? value)
    {
        GuardDispose();
        value = ReadSlot(null, filtered: false, address, index, selfDestructStateIdx);
    }

    public void GetSlot(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key, out UInt256? value) =>
        GetSlot(key.Key.Item1, key.Key.Item2, selfDestructStateIdx, out value);

    /// <summary>
    /// Returns exactly what <see cref="GetSlot(int, HashedKey{ValueTuple{Address, UInt256}}, out UInt256?)"/> returns,
    /// but through the negative filter of the native bundle over its snapshots' slots; for read-only execution only.
    /// </summary>
    public void GetSlotFiltered(int selfDestructStateIdx, HashedKey<(Address, UInt256)> key, out UInt256? value)
    {
        GuardDispose();
        value = ReadSlot(null, filtered: true, key.Key.Item1, key.Key.Item2, selfDestructStateIdx);
    }

    /// <summary>Always <c>false</c>: the native snapshots hold RLP, which <see cref="TryLoadStateRlp"/> loads.</summary>
    public bool TryFindStateNodes(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryFindStateNodes(path, out node);

    /// <inheritdoc cref="TryFindStateNodes(in TreePath, Hash256, out TrieNode?)"/>
    public bool TryFindStateNodes(HashedKey<TreePath> key, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();
        node = null;
        return false;
    }

    /// <summary>Always <c>false</c>: the native snapshots hold RLP, which <see cref="TryLoadStorageRlp"/> loads.</summary>
    public bool TryFindStorageNodes(Hash256 address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node) =>
        TryFindStorageNodes((address, path), out node);

    /// <inheritdoc cref="TryFindStorageNodes(Hash256, in TreePath, Hash256, out TrieNode?)"/>
    public bool TryFindStorageNodes(HashedKey<(Hash256, TreePath)> key, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();
        node = null;
        return false;
    }

    /// <summary>The RLP of the newest state trie node at <paramref name="path"/>, whatever its hash.</summary>
    /// <remarks>As the C# read-only bundle returned its path-keyed node: the caller checks the hash if it needs to.</remarks>
    public byte[]? TryLoadStateRlp(in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();
        return LoadRlp(null, null, path, hash, flags);
    }

    /// <inheritdoc cref="TryLoadStateRlp"/>
    public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();
        return LoadRlp(null, address, path, hash, flags);
    }

    /// <summary>
    /// The native scope a <see cref="SnapshotBundle"/> over this bundle writes: the scope this bundle is the base view
    /// of, opened together with it, or a new one over it, seeded with <paramref name="own"/>.
    /// </summary>
    /// <param name="own">The snapshots the scope starts with, oldest first; the caller keeps its leases on them.</param>
    /// <param name="filterInMemorySlotReads">Whether slot reads below the scope go through the bundle's slot filter;
    /// for a scope opened with this bundle, its open decided that.</param>
    /// <param name="mayHaveSelfDestructs">Whether a snapshot of the bundle or of <paramref name="own"/> clears a storage.</param>
    internal unsafe ScopeHandle TakeOrCreateScope(SnapshotPooledList? own, bool filterInMemorySlotReads, ResourcePool.Usage usage, out bool mayHaveSelfDestructs)
    {
        int ownCount = own?.Count ?? 0;
        if (_handle is ScopeHandle opened && ownCount == 0 && Interlocked.Exchange(ref _scopeTaken, 1) == 0)
        {
            mayHaveSelfDestructs = _hasSelfDestructs;
            return opened;
        }

        RustFlatStore store = _handle.Store;
        ManagerHandle manager = _handle.Manager;
        store.AddChildReference();
        bool managerAdded = false;
        try
        {
            manager.DangerousAddRef(ref managerAdded);
            FfiScopeFlags flags = new(filterInMemorySlotReads && MayFilterSlots, usage);
            Span<nint> ownSnapshots = ownCount <= MaxStackSeedCount ? stackalloc nint[ownCount] : new nint[ownCount];
            for (int i = 0; i < ownCount; i++) ownSnapshots[i] = own![i].Handle.DangerousGetHandle();

            nint scope;
            FfiRobInfo info;
            using (BundleBaseHandle.Call call = _handle.Enter())
            {
                fixed (nint* ownPointers = ownSnapshots)
                {
                    store.ThrowIfFailed(NativeMethods.fdb_scope_create(call.Bundle, ownPointers, (uint)ownCount, &flags, &scope, &info));
                }
            }

            mayHaveSelfDestructs = info.HasSelfDestructs != 0;
            return new ScopeHandle(store, manager, scope);
        }
        catch
        {
            if (managerAdded) manager.DangerousRelease();
            store.ReleaseChildReference();
            throw;
        }
    }

    /// <summary>An account read of <paramref name="scope"/>, a scope over this bundle: its current content and own snapshots first.</summary>
    /// <param name="promote">Whether an account read below the current content is cached in it.</param>
    internal Account? ReadScopeAccount(ScopeHandle scope, bool promote, Address address) => ReadAccount(scope, promote, address);

    /// <summary>A slot read of <paramref name="scope"/>, a scope over this bundle, at the caller's self-destruct index.</summary>
    internal UInt256? ReadScopeSlot(ScopeHandle scope, Address address, in UInt256 index, int selfDestructStateIdx) =>
        ReadSlot(scope, filtered: false, address, index, selfDestructStateIdx);

    /// <summary>
    /// A hash-checked trie node load of <paramref name="scope"/>, a scope over this bundle, of the state trie, or of the
    /// storage trie of <paramref name="address"/> when it is not null.
    /// </summary>
    /// <exception cref="NodeHashMismatchException">The scope's first in-memory node at the path has another hash.</exception>
    internal byte[]? LoadScopeRlp(ScopeHandle scope, Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags) =>
        LoadRlp(scope, address, path, hash, flags);

    // A null scope reads this bundle's handle with the fdb_rob_* exports, so a scope export only ever gets a scope.
    private unsafe Account? ReadAccount(ScopeHandle? scope, bool promote, Address address)
    {
        BundleBaseHandle bundle = (BundleBaseHandle?)scope ?? _handle;
        long start = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        FfiAccount account;
        byte found;
        ReadSource source;
        using (BundleBaseHandle.Call call = bundle.Enter())
        {
            fixed (byte* addressBytes = address.Bytes)
            {
                bundle.Store.ThrowIfFailed(scope is null
                    ? NativeMethods.fdb_rob_get_account(call.Bundle, addressBytes, &account, &found, &source)
                    : NativeMethods.fdb_scope_get_account(call.Bundle, addressBytes, promote ? (byte)1 : (byte)0, &account, &found, &source));
            }
        }

        if (_recordDetailedMetrics && source is ReadSource.Snapshot or ReadSource.Persisted or ReadSource.Persistence)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start,
                source == ReadSource.Snapshot ? _readAccountSnapshotLabel
                : source == ReadSource.Persisted ? _readAccountPersistedLabel
                : found == 0 ? _readAccountPersistenceNullLabel
                : _readAccountPersistenceLabel);
        }

        return found == 0 ? null : account.ToAccount();
    }

    // As ReadAccount; filtered applies to this bundle's reads only.
    private unsafe UInt256? ReadSlot(ScopeHandle? scope, bool filtered, Address address, in UInt256 index, int selfDestructStateIdx)
    {
        BundleBaseHandle bundle = (BundleBaseHandle?)scope ?? _handle;
        long start = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        ulong* slot = stackalloc ulong[4] { index.u0, index.u1, index.u2, index.u3 };
        ulong* value = stackalloc ulong[4];
        byte found;
        ReadSource source;
        using (BundleBaseHandle.Call call = bundle.Enter())
        {
            fixed (byte* addressBytes = address.Bytes)
            {
                bundle.Store.ThrowIfFailed(scope is null
                    ? NativeMethods.fdb_rob_get_slot(call.Bundle, addressBytes, slot, selfDestructStateIdx, filtered ? (byte)1 : (byte)0, value, &found, &source)
                    : NativeMethods.fdb_scope_get_slot(call.Bundle, addressBytes, slot, selfDestructStateIdx, value, &found, &source));
            }
        }

        UInt256? slotValue = found == 0 ? null : new UInt256(value[0], value[1], value[2], value[3]);
        if (_recordDetailedMetrics && source is ReadSource.Snapshot or ReadSource.Persisted or ReadSource.Persistence)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start,
                source == ReadSource.Snapshot ? _readStorageSnapshotLabel
                : source == ReadSource.Persisted ? _readStoragePersistedLabel
                : slotValue.GetValueOrDefault().IsZero ? _readStoragePersistenceNullLabel
                : _readStoragePersistenceLabel);
        }

        return slotValue;
    }

    // As ReadAccount: this bundle's loads are unchecked, a scope's hash-checked.
    private unsafe byte[]? LoadRlp(ScopeHandle? scope, Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        BundleBaseHandle bundle = (BundleBaseHandle?)scope ?? _handle;
        bool isScope = scope is not null;
        long start = _recordDetailedMetrics ? Stopwatch.GetTimestamp() : 0;
        byte* stackBuffer = stackalloc byte[StackBufferSize];
        byte[]? heapBuffer = null;
        long length;
        ReadSource source;
        using (BundleBaseHandle.Call call = bundle.Enter())
        {
            FdbStatus status = Load(call.Bundle, isScope, address, path, hash, flags, stackBuffer, StackBufferSize, &length, &source);
            // A scope's node can be replaced between the calls, so the retry may find a larger one again.
            while (status == FdbStatus.BufferTooSmall)
            {
                heapBuffer = new byte[length];
                fixed (byte* heap = heapBuffer)
                {
                    status = Load(call.Bundle, isScope, address, path, hash, flags, heap, (nuint)heapBuffer.Length, &length, &source);
                }
            }

            bundle.Store.ThrowIfFailed(status);
        }

        RecordNodeLoad(source, address is not null, start);
        if (length < 0) return null;
        return heapBuffer is null ? new ReadOnlySpan<byte>(stackBuffer, (int)length).ToArray()
            : length == heapBuffer.Length ? heapBuffer
            : heapBuffer[..(int)length];
    }

    protected override void CleanUp()
    {
        _isDisposed = true;
        _handle.DangerousRelease();
        if (Volatile.Read(ref _scopeTaken) == 0) _handle.Dispose();
    }

    private static unsafe FdbStatus Load(nint bundle, bool scope, Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags,
        byte* buffer, nuint capacity, long* length, ReadSource* source)
    {
        ValueHash256 nodeHash = hash.ValueHash256;
        ValueHash256 addressHash = address?.ValueHash256 ?? default;
        fixed (byte* pathBytes = path.Path.Bytes, hashBytes = nodeHash.Bytes, addressBytes = addressHash.Bytes)
        {
            byte pathLength = (byte)path.Length;
            return (scope, address is null) switch
            {
                (false, true) => NativeMethods.fdb_rob_load_state_rlp(bundle, pathBytes, pathLength, hashBytes, (uint)flags, buffer, capacity, length, source),
                (false, false) => NativeMethods.fdb_rob_load_storage_rlp(bundle, addressBytes, pathBytes, pathLength, hashBytes, (uint)flags, buffer, capacity, length, source),
                (true, true) => NativeMethods.fdb_scope_load_state_rlp(bundle, pathBytes, pathLength, hashBytes, (uint)flags, buffer, capacity, length, source),
                (true, false) => NativeMethods.fdb_scope_load_storage_rlp(bundle, addressBytes, pathBytes, pathLength, hashBytes, (uint)flags, buffer, capacity, length, source),
            };
        }
    }

    /// <remarks>
    /// In-memory hits count as loaded from cache and persistence reads as loaded from the database, as the C# bundles
    /// count them, and persisted-snapshot hits as neither, as <c>PersistedSnapshotStack</c> counts them; only the reads
    /// C# served from its read-only bundle are timed.
    /// </remarks>
    private void RecordNodeLoad(ReadSource source, bool isStorage, long start)
    {
        if (source == ReadSource.Persistence) Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromDbNodesCount();
        else if (source != ReadSource.Persisted) Nethermind.Trie.Pruning.Metrics.IncrementLoadedFromCacheNodesCount();

        if (_recordDetailedMetrics && source is ReadSource.Snapshot or ReadSource.Persisted or ReadSource.Persistence)
        {
            Metrics.ReadOnlySnapshotBundleTimes.Observe(Stopwatch.GetTimestamp() - start, (source, isStorage) switch
            {
                (ReadSource.Snapshot, false) => _readStateNodeSnapshotLabel,
                (ReadSource.Snapshot, true) => _readStorageNodeSnapshotLabel,
                (ReadSource.Persisted, false) => _readStateRlpPersistedLabel,
                (ReadSource.Persisted, true) => _readStorageRlpPersistedLabel,
                (_, false) => _readStateRlpLabel,
                (_, true) => _readStorageRlpLabel,
            });
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
