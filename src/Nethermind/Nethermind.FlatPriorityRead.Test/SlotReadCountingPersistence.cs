// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.FlatPriorityRead.Test;

/// <summary>Counts the <see cref="IPersistence.IPersistenceReader.TryGetSlot"/> calls that reach the persistence it wraps.</summary>
public sealed class SlotReadCountingPersistence(IPersistence inner) : IPersistence
{
    private int _slotReads;

    public int SlotReads => Volatile.Read(ref _slotReads);

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None) => new Reader(this, inner.CreateReader(flags));
    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None) => inner.CreateWriteBatch(from, to, flags);
    public void Flush() => inner.Flush();
    public void Clear() => inner.Clear();

    private sealed class Reader(SlotReadCountingPersistence parent, IPersistence.IPersistenceReader inner) : IPersistence.IPersistenceReader
    {
        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            Interlocked.Increment(ref parent._slotReads);
            return inner.TryGetSlot(address, slot, ref outValue);
        }

        public Account? GetAccount(Address address) => inner.GetAccount(address);
        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);
        public bool TryLoadStateRlp<TArg>(in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => inner.TryLoadStateRlp(path, flags, action, arg);
        public bool TryLoadStorageRlp<TArg>(Hash256 address, in TreePath path, ReadFlags flags, ReadOnlySpanAction<byte, TArg> action, TArg arg) where TArg : allows ref struct => inner.TryLoadStorageRlp(address, path, flags, action, arg);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }
}
