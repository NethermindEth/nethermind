// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// <see cref="IPersistence"/> in the native Rust library: the layout persistence, the shared reader cache and, when
/// enabled, the carry-forward cache, stacked as FlatWorldStateModule stacks their C# counterparts, over the same
/// <see cref="IColumnsDb{TKey}"/>.
/// </summary>
/// <remarks>
/// Readers, iterators and batches keep the native store alive, so disposing the persistence while one is open defers
/// the release of the store, and the lease on the database, until the last of them is disposed. The metrics the store
/// gathers are published whenever a reader or batch is disposed.
/// </remarks>
internal sealed class RustPersistence : IPersistence, IDisposable, IAsyncDisposable
{
    /// <summary>The warning of <c>BasePersistence.ResolveSlotEncoding</c>.</summary>
    private const string RawSlotDeprecationMessage =
        "Flat DB uses the legacy raw storage slot encoding, which is deprecated and will be removed in a future release. Please resync to adopt the RLP slot encoding.";

    private readonly RustFlatStore _store;

    public RustPersistence(IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
    {
        _store = RustFlatStore.Create(db, config, logManager);

        ILogger logger = logManager.GetClassLogger<RustPersistence>();
        if (!_store.RlpWrapSlots && logger.IsWarn) logger.Warn(RawSlotDeprecationMessage);
    }

    internal RustFlatStore Store => _store;

    public unsafe IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        _store.AddChildReference();
        try
        {
            nint reader;
            FfiStateId currentState;
            byte detailedMetrics = Nethermind.Db.Metrics.DetailedMetricsEnabled ? (byte)1 : (byte)0;
            _store.ThrowIfFailed(NativeMethods.fdb_reader_open(_store, (uint)flags, detailedMetrics, &reader, &currentState));
            return new Reader(_store, new ReaderHandle(_store, reader), currentState.ToStateId());
        }
        catch
        {
            _store.ReleaseChildReference();
            throw;
        }
    }

    public unsafe IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
    {
        FfiStateId nativeFrom = new(from);
        FfiStateId nativeTo = new(to);
        _store.AddChildReference();
        try
        {
            nint batch;
            FfiStateId currentState;
            FdbStatus status = NativeMethods.fdb_batch_open(_store, &nativeFrom, &nativeTo, (uint)flags, &batch, &currentState);
            if (status == FdbStatus.WrongBaseState)
            {
                throw new InvalidOperationException($"Attempted to apply snapshot on top of wrong state. Snapshot from: {from}, Db state: {currentState.ToStateId()}");
            }

            _store.ThrowIfFailed(status);
            return new RustWriteBatch(_store, new BatchHandle(_store, batch));
        }
        catch
        {
            _store.ReleaseChildReference();
            throw;
        }
    }

    public void Flush() => _store.ThrowIfFailed(NativeMethods.fdb_store_flush(_store));

    public void Clear()
    {
        _store.ThrowIfFailed(NativeMethods.fdb_store_clear(_store));
        _store.PublishMetrics();
    }

    public void Dispose() => _store.Dispose();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A native reader, at the state it was opened at.</summary>
    private sealed unsafe class Reader(RustFlatStore store, ReaderHandle handle, StateId currentState) : IPersistence.IPersistenceReader
    {
        /// <summary>Large enough for any trie node but a few, which are read again into an array of their size.</summary>
        private const int StackBufferSize = 1024;

        public StateId CurrentState => currentState;

        public bool IsPreimageMode
        {
            get
            {
                byte preimage;
                store.ThrowIfFailed(NativeMethods.fdb_reader_is_preimage_mode(handle, &preimage));
                return preimage != 0;
            }
        }

        public void Dispose()
        {
            handle.Dispose();
            store.PublishMetrics();
        }

        public Account? GetAccount(Address address)
        {
            FfiAccount account;
            byte found;
            fixed (byte* addressBytes = address.Bytes)
            {
                store.ThrowIfFailed(NativeMethods.fdb_reader_get_account(handle, addressBytes, &account, &found));
            }

            return found == 0 ? null : account.ToAccount();
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            ulong* slotLimbs = stackalloc ulong[4] { slot.u0, slot.u1, slot.u2, slot.u3 };
            ulong* value = stackalloc ulong[4];
            byte found;
            fixed (byte* addressBytes = address.Bytes)
            {
                store.ThrowIfFailed(NativeMethods.fdb_reader_get_slot(handle, addressBytes, slotLimbs, value, &found));
            }

            if (found == 0) return false;
            outValue = new UInt256(value[0], value[1], value[2], value[3]);
            return true;
        }

        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value)
        {
            ulong* read = stackalloc ulong[4];
            byte found;
            fixed (byte* address = addrHash.Bytes, slot = slotHash.Bytes)
            {
                store.ThrowIfFailed(NativeMethods.fdb_reader_get_storage_raw(handle, address, slot, read, &found));
            }

            if (found == 0) return false;
            value = new UInt256(read[0], read[1], read[2], read[3]);
            return true;
        }

        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) =>
            ReadBytes(new BytesRead(BytesRead.Kind.StateRlp, default, path.Path, (byte)path.Length, flags));

        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) =>
            ReadBytes(new BytesRead(BytesRead.Kind.StorageRlp, address.ValueHash256, path.Path, (byte)path.Length, flags));

        public byte[]? GetAccountRaw(in ValueHash256 addrHash) =>
            ReadBytes(new BytesRead(BytesRead.Kind.AccountRaw, addrHash, default, 0, ReadFlags.None));

        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey)
        {
            store.AddChildReference();
            try
            {
                nint iterator;
                fixed (byte* start = startKey.Bytes, end = endKey.Bytes)
                {
                    store.ThrowIfFailed(NativeMethods.fdb_reader_account_iter(handle, start, end, &iterator));
                }

                return new FlatIterator(store, new IteratorHandle(store, iterator));
            }
            catch
            {
                store.ReleaseChildReference();
                throw;
            }
        }

        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey)
        {
            store.AddChildReference();
            try
            {
                nint iterator;
                fixed (byte* account = accountKey.Bytes, start = startSlotKey.Bytes, end = endSlotKey.Bytes)
                {
                    store.ThrowIfFailed(NativeMethods.fdb_reader_storage_iter(handle, account, start, end, &iterator));
                }

                return new FlatIterator(store, new IteratorHandle(store, iterator));
            }
            catch
            {
                store.ReleaseChildReference();
                throw;
            }
        }

        /// <summary>Reads a byte string into a stack buffer, or when it is larger, into an array of its size.</summary>
        private byte[]? ReadBytes(in BytesRead read)
        {
            byte* buffer = stackalloc byte[StackBufferSize];
            long length;
            FdbStatus status = Read(read, buffer, StackBufferSize, &length);
            if (status != FdbStatus.BufferTooSmall)
            {
                store.ThrowIfFailed(status);
                return length < 0 ? null : new ReadOnlySpan<byte>(buffer, (int)length).ToArray();
            }

            // The reader reads a snapshot, so the value has the length just reported.
            byte[] value = new byte[length];
            fixed (byte* target = value)
            {
                store.ThrowIfFailed(Read(read, target, (nuint)value.Length, &length));
            }

            return length < 0 ? null : length == value.Length ? value : value[..(int)length];
        }

        private FdbStatus Read(in BytesRead read, byte* buffer, nuint capacity, long* length)
        {
            fixed (byte* hash = read.Hash.Bytes, path = read.Path.Bytes)
            {
                return read.What switch
                {
                    BytesRead.Kind.StateRlp => NativeMethods.fdb_reader_load_state_rlp(handle, path, read.PathLength, (uint)read.Flags, buffer, capacity, length),
                    BytesRead.Kind.StorageRlp => NativeMethods.fdb_reader_load_storage_rlp(handle, hash, path, read.PathLength, (uint)read.Flags, buffer, capacity, length),
                    _ => NativeMethods.fdb_reader_get_account_raw(handle, hash, buffer, capacity, length),
                };
            }
        }
    }

    /// <summary>A read of a byte string: what is read, and its arguments.</summary>
    private readonly struct BytesRead(BytesRead.Kind what, in ValueHash256 hash, in ValueHash256 path, byte pathLength, ReadFlags flags)
    {
        public enum Kind
        {
            StateRlp,
            StorageRlp,
            AccountRaw,
        }

        public readonly Kind What = what;
        public readonly ValueHash256 Hash = hash;
        public readonly ValueHash256 Path = path;
        public readonly byte PathLength = pathLength;
        public readonly ReadFlags Flags = flags;
    }

    /// <summary>A native iterator; the current value points into native memory until the next move.</summary>
    private sealed unsafe class FlatIterator(RustFlatStore store, IteratorHandle handle) : IPersistence.IFlatIterator
    {
        private ValueHash256 _currentKey;
        private byte* _currentValue;
        private int _currentValueLength;

        public ValueHash256 CurrentKey => _currentKey;

        public ReadOnlySpan<byte> CurrentValue => new(_currentValue, _currentValueLength);

        public bool MoveNext()
        {
            ValueHash256 key;
            byte* value;
            nuint valueLength;
            byte hasEntry;
            store.ThrowIfFailed(NativeMethods.fdb_iter_next(handle, (byte*)&key, &value, &valueLength, &hasEntry));
            if (hasEntry == 0) return false;

            _currentKey = key;
            _currentValue = value;
            _currentValueLength = (int)valueLength;
            return true;
        }

        public void Dispose() => handle.Dispose();
    }
}
