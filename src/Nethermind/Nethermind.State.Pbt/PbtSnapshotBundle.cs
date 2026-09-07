// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>A writable canonical branch over sealed local snapshots and a shared read-only base.</summary>
public sealed class PbtSnapshotBundle(
    PbtSnapshotPooledList snapshots,
    PbtReadOnlySnapshotBundle readOnlyBundle,
    IPbtResourcePool resourcePool,
    PbtResourcePool.Usage usage) : IDisposable
{
    private PbtSnapshotContent? _writeBuffer = resourcePool.GetSnapshotContent(usage);
    private PbtPendingFlatWrites? _pending = resourcePool.GetPendingFlatWrites(usage);
    private PbtWriteBatchBuilder? _writeBatchBuilder = resourcePool.GetWriteBatchBuilder(usage);
    private bool _isDisposed;

    public ValueHash256 TreeRoot => snapshots.Count > 0 ? snapshots[^1].TreeRoot : readOnlyBundle.TreeRoot;

    internal Dictionary<ValueHash256, byte[]> PendingCode { get; } = [];

    private PbtSnapshotContent WriteBuffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBuffer!;
        }
    }

    private PbtPendingFlatWrites Pending
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _pending!;
        }
    }

    private PbtWriteBatchBuilder WriteBatchBuilder
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _writeBatchBuilder!;
        }
    }

    internal ValueHash256? GetLeaf(PbtFullKey key)
    {
        if (WriteBatchBuilder.TryGetLeaf(key, out ValueHash256? value)) return value;
        if (WriteBuffer.TryGetLeaf(key, out value)) return value;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeaf(key, out value)) return value;
        }

        return readOnlyBundle.GetLeaf(key);
    }

    internal void SetLeaf(PbtFullKey key, ValueHash256? value) => WriteBatchBuilder.SetLeaf(key, value);

    internal PbtWriteBatchSet PrepareLeafChanges() => WriteBatchBuilder.PrepareDrain();

    internal void CompleteLeafChanges()
    {
        foreach ((PbtFullKey key, ValueHash256? value) in WriteBatchBuilder.Leaves) WriteBuffer.SetLeaf(key, value);
        WriteBatchBuilder.CompleteDrain();
    }

    internal void SetNodeGroup(PbtNodePath groupKey, RefCountingMemory? payload) => WriteBuffer.SetNodeGroup(groupKey, payload);

    internal RefCountingMemory? GetNodeGroup(PbtNodePath groupKey)
    {
        ArgumentNullException.ThrowIfNull(groupKey);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));
        if (WriteBuffer.TryGetNodeGroup(groupKey, out RefCountingMemory? payload)) return payload;
        for (int index = snapshots.Count - 1; index >= 0; index--)
            if (snapshots[index].Content.TryGetNodeGroup(groupKey, out payload)) return payload;
        return readOnlyBundle.GetNodeGroup(groupKey);
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        if (WriteBuffer.TryGetCodeReference(codeHash, out ulong? count)) return count ?? 0;
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetCodeReference(codeHash, out count)) return count ?? 0;
        }

        return readOnlyBundle.GetCodeReference(codeHash);
    }

    internal void SetCodeReference(in ValueHash256 codeHash, ulong? referenceCount) =>
        WriteBuffer.SetCodeReference(codeHash, referenceCount);

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256?>> PendingLeafMutations()
    {
        foreach (KeyValuePair<PbtFullKey, ValueHash256?> entry in WriteBuffer.Leaves)
            if (!WriteBatchBuilder.TryGetLeaf(entry.Key, out _)) yield return entry;
        foreach (KeyValuePair<PbtFullKey, ValueHash256?> entry in WriteBatchBuilder.Leaves) yield return entry;
    }

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => EnumerateLeavesCore(null);

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => EnumerateLeavesCore(prefix);

    private IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeavesCore(PbtFullKey? prefix)
    {
        SortedDictionary<PbtFullKey, ValueHash256?> visible = [];
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> shared = prefix is null
            ? readOnlyBundle.EnumerateLeaves()
            : readOnlyBundle.EnumerateLeaves(prefix.Value);
        foreach ((PbtFullKey key, ValueHash256 value) in shared) visible[key] = value;
        for (int i = 0; i < snapshots.Count; i++) AddLeaves(visible, snapshots[i].Content, prefix);
        AddLeaves(visible, WriteBuffer, prefix);
        WriteBatchBuilder.CopyLeavesTo(visible, prefix);
        foreach ((PbtFullKey key, ValueHash256? value) in visible)
        {
            if (value is not null) yield return new KeyValuePair<PbtFullKey, ValueHash256>(key, value.Value);
        }
    }

    private static void AddLeaves(SortedDictionary<PbtFullKey, ValueHash256?> visible, PbtSnapshotContent content, PbtFullKey? prefix)
    {
        foreach ((PbtFullKey key, ValueHash256? value) in content.Leaves)
        {
            if (prefix is null || prefix.Value.IsPrefixOf(key)) visible[key] = value;
        }
    }

    internal bool AnyLeaf(PbtFullKey prefix)
    {
        foreach (KeyValuePair<PbtFullKey, ValueHash256> _ in EnumerateLeaves(prefix)) return true;
        return false;
    }

    internal void DeletePrefix(PbtFullKey prefix)
    {
        foreach ((PbtFullKey key, _) in EnumerateLeaves(prefix)) SetLeaf(key, null);
    }

    public Account? GetAccount(Address address)
    {
        if (Pending.Accounts.TryGetValue(address, out Account? pending)) return pending;
        ValueHash256? basicData = GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey));
        ValueHash256? codeHash = GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey));
        if (basicData is null && codeHash is null) return null;
        ulong nonce = 0;
        UInt256 balance = default;
        if (basicData is not null) PbtKeyDerivation.UnpackBasicData(basicData.Value.Bytes, out nonce, out balance);
        return new Account(nonce, balance, Keccak.EmptyTreeHash,
            codeHash is null ? Keccak.OfAnEmptyString : new Hash256(codeHash.Value.Bytes));
    }

    public EvmWord GetSlot(Address address, in UInt256 slot)
    {
        if (Pending.Slots.TryGetValue((address, slot), out EvmWord pending)) return pending;
        if (Pending.SelfDestructs.ContainsKey(address)) return default;
        ValueHash256? value = GetLeaf(PbtStateKey.Storage(address, slot));
        return value is null ? default : EvmWordSlot.FromStripped(value.Value.Bytes);
    }

    public void SetAccount(Address address, Account? account) => Pending.Accounts[address] = account;

    internal void PromoteAccount(Address address, Account? account) => Pending.Accounts.TryAdd(address, account);

    public void SetSlot(Address address, in UInt256 slot, in EvmWord value) => Pending.Slots[(address, slot)] = value;

    public void SelfDestruct(Address address)
    {
        foreach (((AddressAsKey Address, UInt256 Slot) key, _) in Pending.Slots)
        {
            if (key.Address.Equals((AddressAsKey)address)) Pending.Slots.TryRemove(key, out _);
        }
        Pending.SelfDestructs[address] = true;
        DeletePrefix(PbtStateKey.StoragePrefix(address));
    }

    internal void ApplyAccount(Address address, Account account)
    {
        ValueHash256 oldCodeHash = GetAccount(address)?.CodeHash.ValueHash256 ?? ValueKeccak.OfAnEmptyString;
        ValueHash256 newCodeHash = account.CodeHash.ValueHash256;
        if (oldCodeHash != newCodeHash)
        {
            RemoveCodeReference(oldCodeHash);
            AddCodeReference(newCodeHash);
        }

        byte[]? code = account.HasCode && PendingCode.TryGetValue(newCodeHash, out byte[]? pending) ? pending : null;
        ValueHash256? prior = GetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey));
        uint priorCodeSize = prior is null ? 0 : PbtKeyDerivation.ReadBasicDataCodeSize(prior.Value.Bytes);
        uint codeSize = code is not null ? (uint)code.Length
            : !account.HasCode ? 0
            : priorCodeSize;
        Span<byte> basicData = stackalloc byte[ValueHash256.MemorySize];
        PbtKeyDerivation.PackBasicData(basicData, codeSize, account.Nonce, account.Balance);
        SetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.BasicDataLeafKey), new ValueHash256(basicData));
        SetLeaf(PbtStateKey.Account(address, PbtKeyDerivation.CodeHashLeafKey), newCodeHash);

        int priorHeaderChunkCount = Math.Min(
            checked((int)((priorCodeSize + 30) / 31)),
            PbtKeyDerivation.HeaderCodeChunks);
        if (code is null)
        {
            if (!account.HasCode)
            {
                for (int chunkId = 0; chunkId < priorHeaderChunkCount; chunkId++)
                    SetLeaf(PbtStateKey.Code(address, oldCodeHash, chunkId), null);
            }
            return;
        }

        byte[] chunks = PbtKeyDerivation.ChunkifyCode(code);
        int count = chunks.Length / PbtKeyDerivation.CodeChunkSize;
        int headerChunkCount = Math.Min(count, PbtKeyDerivation.HeaderCodeChunks);
        for (int chunkId = headerChunkCount; chunkId < priorHeaderChunkCount; chunkId++)
            SetLeaf(PbtStateKey.Code(address, oldCodeHash, chunkId), null);
        for (int chunkId = 0; chunkId < count; chunkId++)
        {
            ReadOnlySpan<byte> chunk = chunks.AsSpan(chunkId * PbtKeyDerivation.CodeChunkSize, PbtKeyDerivation.CodeChunkSize);
            PbtFullKey key = PbtStateKey.Code(address, newCodeHash, chunkId);
            SetLeaf(key, chunk.IndexOfAnyExcept((byte)0) < 0 ? null : new ValueHash256(chunk));
        }
    }

    internal void DeleteAccount(Address address)
    {
        Account? prior = GetAccount(address);
        if (prior is not null) RemoveCodeReference(prior.CodeHash.ValueHash256);
        DeletePrefix(PbtStateKey.AccountPrefix(address));
        DeletePrefix(PbtStateKey.StoragePrefix(address));
        SelfDestruct(address);
    }

    private void AddCodeReference(in ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString) return;
        SetCodeReference(codeHash, checked(GetCodeReference(codeHash) + 1));
    }

    private void RemoveCodeReference(in ValueHash256 codeHash)
    {
        if (codeHash == ValueKeccak.OfAnEmptyString) return;
        ulong count = GetCodeReference(codeHash);
        if (count <= 1)
        {
            SetCodeReference(codeHash, null);
            DeletePrefix(PbtStateKey.OverflowCodePrefix(codeHash));
        }
        else
        {
            SetCodeReference(codeHash, count - 1);
        }
    }

    public PbtSnapshot CollectSnapshot(in StateId from, in StateId to, in ValueHash256 treeRoot)
    {
        foreach (KeyValuePair<PbtFullKey, ValueHash256?> _ in WriteBatchBuilder.Leaves)
            throw new InvalidOperationException("Pending leaf changes must be folded before collecting a snapshot.");
        PbtSnapshot snapshot = new(from, to, treeRoot, WriteBuffer, resourcePool, usage);
        snapshot.TryLease();
        snapshots.Add(snapshot);
        _writeBuffer = resourcePool.GetSnapshotContent(usage);
        Pending.Reset();
        PendingCode.Clear();
        return snapshot;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        PendingCode.Clear();
        PbtSnapshotContent? buffer = _writeBuffer;
        _writeBuffer = null;
        PbtWriteBatchBuilder? builder = _writeBatchBuilder;
        _writeBatchBuilder = null;
        PbtPendingFlatWrites? pending = _pending;
        _pending = null;
        try
        {
            snapshots.Dispose();
            if (buffer is not null) resourcePool.ReturnSnapshotContent(usage, buffer);
            if (pending is not null) resourcePool.ReturnPendingFlatWrites(usage, pending);
        }
        finally
        {
            try
            {
                if (builder is not null) resourcePool.ReturnWriteBatchBuilder(usage, builder);
            }
            finally
            {
                readOnlyBundle.Dispose();
            }
        }
    }
}
