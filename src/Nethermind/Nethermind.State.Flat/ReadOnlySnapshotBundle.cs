// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Utils;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat;

/// <summary>
/// A bundle of <see cref="Snapshot"/> and a layer of write buffer backed by a <see cref="SnapshotContent"/>.
/// </summary>
public sealed class ReadOnlySnapshotBundle(
    SnapshotPooledList snapshots,
    IPersistence.IPersistenceReader persistenceReader)
    : RefCountingDisposable
{
    public int SnapshotCount => snapshots.Count;
    private bool _isDisposed;

    public bool TryGetAccount(Address address, out Account? acc)
    {
        GuardDispose();

        AddressAsKey key = address;

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetAccount(key, out acc))
            {
                return true;
            }
        }

        acc = persistenceReader.GetAccount(address);
        return true;
    }

    public int DetermineSelfDestructSnapshotIdx(Address address)
    {
        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].HasSelfDestruct(address))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryGetSlot(Address address, in UInt256 index, int selfDestructStateIdx, out byte[]? value)
    {
        GuardDispose();

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStorage(address, index, out SlotValue? slotValue))
            {
                if (slotValue is null)
                {
                    value = null;
                }
                else
                {
                    value = slotValue.Value.ToEvmBytes();
                }
                return true;
            }

            if (i <= selfDestructStateIdx)
            {
                value = null;
                return true;
            }
        }

        SlotValue outSlotValue = new SlotValue();

        bool _ = persistenceReader.TryGetSlot(address, index, ref outSlotValue);
        value = outSlotValue.ToEvmBytes();

        return true;
    }

    public bool TryFindStateNodes(in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
    {
        GuardDispose();

        for (int i = snapshots.Count - 1; i >= 0; i--)
        {
            if (snapshots[i].TryGetStateNode(path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return true;
            }
        }

        node = null;
        return false;
    }

    public bool TryFindStorageNodes(Hash256AsKey address, in TreePath path, Hash256 hash, int selfDestructStateIdx, [NotNullWhen(true)] out TrieNode? node)
    {
        for (int i = snapshots.Count - 1; i >= 0 && i >= selfDestructStateIdx; i--)
        {
            if (snapshots[i].TryGetStorageNode(address, path, out node))
            {
                Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
                return true;
            }
        }

        if (selfDestructStateIdx != -1)
        {
            // If there is a self destruct, there is no need to check further, return true
            Nethermind.Trie.Pruning.Metrics.LoadedFromCacheNodesCount++;
            node = null;
            return false;
        }

        node = null;
        return false;
    }

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        GuardDispose();

        Nethermind.Trie.Pruning.Metrics.LoadedFromDbNodesCount++;
        return persistenceReader.TryLoadRlp(address, path, flags);
    }

    private void GuardDispose()
    {
        if (_isDisposed) throw new ObjectDisposedException($"{nameof(ReadOnlySnapshotBundle)} is disposed");
    }

    public bool TryLease()
    {
        return TryAcquireLease();
    }

    protected override void CleanUp()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, true, false)) return;

        snapshots.Dispose();

        // Null them in case unexpected mutation from trie warmer
        persistenceReader.Dispose();
    }
}
