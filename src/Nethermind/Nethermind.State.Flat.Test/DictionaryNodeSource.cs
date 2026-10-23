// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.NodeBuffer;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.Test;

/// <summary>A node source over path-keyed node RLP, for tests and benchmarks: a node <paramref name="warmed"/> holds is served from it, otherwise it is fetched and verified.</summary>
/// <param name="address">The storage trie's address hash, or null for the state trie.</param>
public sealed class DictionaryNodeSource(IReadOnlyDictionary<TreePath, byte[]> nodes, WarmedNodeBuffer warmed, Hash256? address)
{
    private int _fetchCount;

    /// <summary>Number of lookups that missed the warmed nodes and went to the dictionary.</summary>
    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>A store that reads and writes <paramref name="buffer"/>, combined with the warmed nodes, and loads the nodes neither holds from this source.</summary>
    public INodeRecordStore Over(INodeRecordBuffer buffer) => new Store(this, new WarmedNodeRecordBuffer(buffer, warmed));

    public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
    {
        if (warmed.TryGet(address, path, out NodeRecord record, out _))
        {
            action(record.Span, arg);
            return true;
        }

        byte[]? rlp = LoadRlp(path);
        return rlp is not null && ValueKeccak.Compute(rlp) == hash && Persistence.Pass(rlp, action, arg);
    }

    /// <param name="buffer">The buffer holding the trie, which the warmed nodes are combined with.</param>
    public void WarmUp(INodeRecordBuffer buffer, (ValueHash256 Hash, ulong Pointer) root, in ValueHash256 key, bool forDelete = false)
    {
        using ReadOnlySnapshotBundle reader = new(new SnapshotPooledList(0), new Persistence(this), false, PersistedSnapshotStack.Empty(), bufferPin: null);
        BufferWarmUp.WarmUp(new WarmedNodeRecordBuffer(buffer, warmed), warmed, reader, address, root, key, forDelete);
    }

    private byte[]? LoadRlp(in TreePath path)
    {
        Interlocked.Increment(ref _fetchCount);
        return nodes.GetValueOrDefault(path);
    }

    /// <summary>Serves the trie node RLP of the dictionary by path, counting each read as a fetch.</summary>
    /// <remarks>Passes RLP as a copy that is scrambled once the callback returns, so a reader keeping the borrowed span fails.</remarks>
    private sealed class Persistence(DictionaryNodeSource source) : IPersistence.IPersistenceReader
    {
        public Account? GetAccount(Address address) => throw new NotSupportedException();
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue) => throw new NotSupportedException();
        public StateId CurrentState => throw new NotSupportedException();
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => source.LoadRlp(path);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => source.LoadRlp(path);

        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TArg : allows ref struct => Pass(source.LoadRlp(path), action, arg);

        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg)
            where TArg : allows ref struct => Pass(source.LoadRlp(path), action, arg);

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => throw new NotSupportedException();
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => throw new NotSupportedException();
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) =>
            throw new NotSupportedException();
        public bool IsPreimageMode => false;
        public void Dispose() { }

        public static bool Pass<TArg>(byte[]? rlp, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct
        {
            if (rlp is null) return false;
            byte[] borrowed = rlp.ToArray();
            action(borrowed, arg);
            borrowed.AsSpan().Fill(0xFF);
            return true;
        }
    }

    private sealed class Store(DictionaryNodeSource source, INodeRecordBuffer buffer) : INodeRecordStore
    {
        public bool TryLocate(ulong pointer, out NodeRecord record) => buffer.TryLocate(pointer, out record);

        public INodeRecordWriteBatch StartWriteBatch(int alignedTotalLength) => buffer.StartWriteBatch(alignedTotalLength);

        public bool TryLoad<TArg>(in TreePath path, in ValueHash256 hash, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct =>
            source.TryLoad(path, hash, action, arg);
    }
}
