// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt;

/// <summary>An immutable canonical state view composed from snapshot diffs over one persistence snapshot.</summary>
public sealed class PbtReadOnlySnapshotBundle(
    PbtSnapshotPooledList snapshots,
    IPbtPersistence.IReader reader) : RefCountingDisposable
{
    private bool _isDisposed;

    public ValueHash256 TreeRoot
    {
        get
        {
            GuardDispose();
            return snapshots.Count > 0 ? snapshots[^1].TreeRoot : reader.CurrentRoot;
        }
    }

    internal ValueHash256? GetLeaf(PbtFullKey key)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetLeaf(key, out ValueHash256? value)) return value;
        }

        return reader.GetLeaf(key);
    }

    internal byte[]? GetNode(PbtNodePath path)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetNode(path, out byte[]? encoding)) return encoding;
        }

        return reader.GetNode(path);
    }

    internal RefCountingMemory? GetNodeGroup(PbtNodePath groupKey) => GetNodeGroup(groupKey, []);

    internal RefCountingMemory? GetNodeGroup(PbtNodePath groupKey, IReadOnlyList<PbtSnapshotContent> additionalLayers)
    {
        GuardDispose();
        ArgumentNullException.ThrowIfNull(groupKey);
        ArgumentNullException.ThrowIfNull(additionalLayers);
        if (!PbtFourLevelGroupGeometry.IsGroupDepth(groupKey.BitDepth))
            throw new ArgumentException("A group key depth must be a four-level boundary.", nameof(groupKey));

        RefCountingMemory? baseLease = reader.GetNodeGroup(groupKey);
        try
        {
            ReadOnlyMemory<byte>[] encodings = new ReadOnlyMemory<byte>[PbtNodeGroupCodec.PositionCount];
            bool[] present = new bool[PbtNodeGroupCodec.PositionCount];
            if (baseLease is not null)
            {
                PbtNodeGroupReader baseReader = new(groupKey, baseLease.GetSpan());
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    if (baseReader.TryGetNodeRange(position, out int offset, out int length))
                    {
                        encodings[position] = baseLease.Memory.Slice(offset, length);
                        present[position] = true;
                    }
                }
            }

            bool changed = false;
            for (int index = 0; index < snapshots.Count; index++)
                changed |= snapshots[index].Content.ApplyNodeGroupDeltas(groupKey, encodings, present);
            for (int index = 0; index < additionalLayers.Count; index++)
                changed |= additionalLayers[index].ApplyNodeGroupDeltas(groupKey, encodings, present);

            if (!changed)
            {
                RefCountingMemory? result = baseLease;
                baseLease = null;
                return result;
            }

            bool anyPresent = false;
            for (int position = 0; position < present.Length; position++) anyPresent |= present[position];
            if (!anyPresent) return null;

            BufferWriter writer = new(PooledRefCountingMemoryProvider.Instance);
            try
            {
                PbtNodeGroupCodec.Encode(ref writer, groupKey, encodings, present);
                return writer.Detach()!;
            }
            finally
            {
                writer.Dispose();
            }
        }
        finally
        {
            ((IDisposable?)baseLease)?.Dispose();
        }
    }

    internal ulong GetCodeReference(in ValueHash256 codeHash)
    {
        GuardDispose();
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].Content.TryGetCodeReference(codeHash, out ulong? count)) return count ?? 0;
        }

        return reader.GetCodeReference(codeHash);
    }

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves() => EnumerateLeavesCore(null);

    internal IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeaves(PbtFullKey prefix) => EnumerateLeavesCore(prefix);

    private IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> EnumerateLeavesCore(PbtFullKey? prefix)
    {
        GuardDispose();
        SortedDictionary<PbtFullKey, ValueHash256?> visible = [];
        IEnumerable<KeyValuePair<PbtFullKey, ValueHash256>> persisted = prefix is null
            ? reader.EnumerateLeaves()
            : reader.EnumerateLeaves(prefix);
        foreach ((PbtFullKey key, ValueHash256 value) in persisted) visible[key] = value;
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach ((PbtFullKey key, ValueHash256? value) in snapshots[i].Content.Leaves)
            {
                if (prefix is null || prefix.IsPrefixOf(key)) visible[key] = value;
            }
        }

        foreach ((PbtFullKey key, ValueHash256? value) in visible)
        {
            if (value is not null) yield return new KeyValuePair<PbtFullKey, ValueHash256>(key, value.Value);
        }
    }

    internal IEnumerable<KeyValuePair<PbtNodePath, byte[]>> EnumerateNodes()
    {
        GuardDispose();
        SortedDictionary<PbtNodePath, byte[]?> visible = [];
        foreach ((PbtNodePath path, byte[] encoding) in reader.EnumerateNodes()) visible[path] = encoding;
        for (int i = 0; i < snapshots.Count; i++)
        {
            foreach ((PbtNodePath path, byte[]? encoding) in snapshots[i].Content.Nodes) visible[path] = encoding;
        }

        foreach ((PbtNodePath path, byte[]? encoding) in visible)
        {
            if (encoding is not null) yield return new KeyValuePair<PbtNodePath, byte[]>(path, encoding);
        }
    }

    internal bool AnyLeaf(PbtFullKey prefix)
    {
        foreach (KeyValuePair<PbtFullKey, ValueHash256> _ in EnumerateLeaves(prefix)) return true;
        return false;
    }

    public Account? GetAccount(Address address)
    {
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
        ValueHash256? value = GetLeaf(PbtStateKey.Storage(address, slot));
        return value is null ? default : EvmWordSlot.FromStripped(value.Value.Bytes);
    }

    public bool TryLease() => TryAcquireLease();

    protected override void CleanUp()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            snapshots.Dispose();
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void GuardDispose() => ObjectDisposedException.ThrowIf(_isDisposed, this);
}
