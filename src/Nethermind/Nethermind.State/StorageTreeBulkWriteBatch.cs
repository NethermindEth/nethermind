// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State;

public class StorageTreeBulkWriteBatch(
    int estimatedEntries,
    StorageTree storageTree,
    Action<Address, Hash256> onRootUpdated,
    AddressAsKey address,
    bool commit = false) : IWorldStateScopeProvider.IStorageWriteBatch
{
    // Slight optimization on small contract as the index hash can be precalculated in some case.
    public const int MIN_ENTRIES_TO_BATCH = 16;

    private bool _hasSelfDestruct;
    private bool _wasSetCalled = false;

    private ArrayPoolList<PatriciaTree.BulkSetEntry>? _bulkWrite =
        estimatedEntries > MIN_ENTRIES_TO_BATCH
            ? new(estimatedEntries)
            : null;

    private ValueHash256 _keyBuff = new();
    private PendingHashes? _pendingHashes;

    private sealed class PendingHashes
    {
        internal KeyHashBatch Batch;
        internal PendingHashes() => Batch.Initialize(Hash256.Size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddUnhashedEntry(ReadOnlySpan<byte> preimage, ReadOnlySpan<byte> encoded, bool isZero)
    {
        PendingHashes pending = _pendingHashes ??= new();
        int index = _bulkWrite!.Count;
        _bulkWrite.Add(StorageTree.CreateBulkSetEntry(default, encoded, isZero));
        pending.Batch.AddMissing(preimage, index);
        if (pending.Batch.IsFull) pending.Batch.Flush(_bulkWrite.AsSpan());
    }

    [SkipLocalsInit]
    public void Set(in UInt256 index, in UInt256 value)
    {
        Unsafe.SkipInit(out EvmWord word);
        bool isZero = value.IsZero;
        ReadOnlySpan<byte> encoded = isZero ? StorageTree.ZeroBytes : value.ToMinimalBigEndian(ref word);
        _wasSetCalled = true;
        if (_bulkWrite is null)
        {
            storageTree.Set(index, encoded, isZero);
        }
        else
        {
            if (Avx2.IsSupported)
            {
                if (!StorageTree.TryGetCachedKey(index, out _keyBuff, out ValueHash256 preimage))
                {
                    AddUnhashedEntry(preimage.BytesAsSpan, encoded, isZero);
                    return;
                }
            }
            else
            {
                StorageTree.ComputeKeyWithLookup(index, ref _keyBuff);
            }
            _bulkWrite.Add(StorageTree.CreateBulkSetEntry(_keyBuff, encoded, isZero));
        }
    }

    public void Clear()
    {
        if (_bulkWrite is null)
        {
            storageTree.RootHash = Keccak.EmptyTreeHash;
        }

        if (_wasSetCalled) throw new InvalidOperationException("Must call clear first in a storage write batch");
        _hasSelfDestruct = true;
    }

    public void Dispose()
    {
        bool hasSet = _wasSetCalled || _hasSelfDestruct;
        int bulkCount = 0;
        if (_bulkWrite is not null)
        {
            if (_hasSelfDestruct)
            {
                storageTree.RootHash = Keccak.EmptyTreeHash;
            }

            _pendingHashes?.Batch.Flush(_bulkWrite.AsSpan());
            _pendingHashes = null;
            bulkCount = _bulkWrite.Count;
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> asRef = _bulkWrite.ToRef();
            storageTree.BulkSet(asRef);
        }

        if (hasSet)
        {
            if (commit)
            {
                storageTree.Commit();
            }
            else
            {
                storageTree.UpdateRootHash(bulkCount > 64);
            }
            onRootUpdated(address, storageTree.RootHash);
        }
    }
}
