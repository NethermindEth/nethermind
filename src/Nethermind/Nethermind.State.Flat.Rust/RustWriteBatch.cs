// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust;

/// <summary>
/// A write batch of the native store. The calls are encoded into the op stream of
/// native/crates/flatdb-ffi/src/op_stream.rs and handed over a buffer at a time; disposing the batch commits it, as
/// disposing a C# batch does.
/// </summary>
/// <remarks>
/// The calls the C# persistence refuses for its layout throw at once, as they do there. Any other failure of an
/// operation surfaces when its buffer is handed over: from a later call, or from <see cref="Dispose"/>, which then
/// still commits the operations before it, as disposing a C# batch after a failed call does.
/// </remarks>
internal sealed unsafe class RustWriteBatch(RustFlatStore store, BatchHandle handle) : IPersistence.IWriteBatch
{
    private const int BufferSize = 64 * 1024;

    private const int AddressSize = Address.Size;
    private const int HashSize = ValueHash256.MemorySize;
    private const int UInt256Size = 32;
    private const int AccountSize = sizeof(ulong) + UInt256Size + 2 * HashSize;
    private const int PathSize = HashSize + 1;
    private const int LengthSize = sizeof(uint);

    private const byte SelfDestructOp = 0;
    private const byte SetAccountOp = 1;
    private const byte RemoveAccountOp = 2;
    private const byte SetStorageOp = 3;
    private const byte RemoveStorageOp = 4;
    private const byte SetStateTrieNodeOp = 5;
    private const byte SetStorageTrieNodeOp = 6;
    private const byte SetStorageRawEncodedOp = 7;
    private const byte SetAccountRawOp = 8;
    private const byte DeleteAccountRangeOp = 9;
    private const byte DeleteStorageRangeOp = 10;
    private const byte DeleteStateTrieNodeRangeOp = 11;
    private const byte DeleteStorageTrieNodeRangeOp = 12;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _length;
    private bool _disposed;

    public void SelfDestruct(Address addr)
    {
        OpWriter op = Append(SelfDestructOp, AddressSize);
        op.Write(addr.Bytes);
    }

    public void SetAccount(Address addr, Account? account)
    {
        if (account is null)
        {
            Append(RemoveAccountOp, AddressSize).Write(addr.Bytes);
            return;
        }

        OpWriter op = Append(SetAccountOp, AddressSize + AccountSize);
        op.Write(addr.Bytes);
        op.Write(account);
    }

    public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
    {
        if (value is null)
        {
            OpWriter remove = Append(RemoveStorageOp, AddressSize + UInt256Size);
            remove.Write(addr.Bytes);
            remove.Write(slot);
            return;
        }

        OpWriter op = Append(SetStorageOp, AddressSize + 2 * UInt256Size);
        op.Write(addr.Bytes);
        op.Write(slot);
        op.Write(value.Value);
    }

    public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp)
    {
        OpWriter op = Append(SetStateTrieNodeOp, PathSize + LengthSize + rlp.Length);
        op.Write(path);
        op.WriteWithLength(rlp);
    }

    public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp)
    {
        OpWriter op = Append(SetStorageTrieNodeOp, HashSize + PathSize + LengthSize + rlp.Length);
        op.Write(address.Bytes);
        op.Write(path);
        op.WriteWithLength(rlp);
    }

    public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
    {
        ThrowIfRawUnavailable();
        if (!store.RlpWrapSlots) throw new NotSupportedException("Encoded slot writes require RLP slot wrapping");

        // As BaseFlatPersistence.SetStorageEncoded: a malformed value throws here, and trailing bytes are dropped.
        RlpReader ctx = new(rlpValue);
        ctx.DecodeByteArraySpan();
        ReadOnlySpan<byte> value = rlpValue[..ctx.Position];

        OpWriter op = Append(SetStorageRawEncodedOp, 2 * HashSize + LengthSize + value.Length);
        op.Write(addrHash.Bytes);
        op.Write(slotHash.Bytes);
        op.WriteWithLength(value);
    }

    public void SetAccountRaw(in ValueHash256 addrHash, Account account)
    {
        ThrowIfRawUnavailable();
        OpWriter op = Append(SetAccountRawOp, HashSize + AccountSize);
        op.Write(addrHash.Bytes);
        op.Write(account);
    }

    public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
    {
        ThrowIfSnapSyncUnavailable();
        OpWriter op = Append(DeleteAccountRangeOp, 2 * HashSize);
        op.Write(fromPath.Bytes);
        op.Write(toPath.Bytes);
    }

    public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
    {
        ThrowIfSnapSyncUnavailable();
        OpWriter op = Append(DeleteStorageRangeOp, 3 * HashSize);
        op.Write(addressHash.Bytes);
        op.Write(fromPath.Bytes);
        op.Write(toPath.Bytes);
    }

    public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to)
    {
        OpWriter op = Append(DeleteStateTrieNodeRangeOp, 2 * HashSize);
        op.Write(from.Bytes);
        op.Write(to.Bytes);
    }

    public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to)
    {
        OpWriter op = Append(DeleteStorageTrieNodeRangeOp, 3 * HashSize);
        op.Write(addressHash.Bytes);
        op.Write(from.Bytes);
        op.Write(to.Bytes);
    }

    /// <summary>Hands over what is buffered and commits the batch.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ExceptionDispatchInfo? applyFailure = null;
        try
        {
            try
            {
                HandOver();
            }
            catch (Exception exception)
            {
                applyFailure = ExceptionDispatchInfo.Capture(exception);
            }

            store.ThrowIfFailed(NativeMethods.fdb_batch_commit(handle));
        }
        finally
        {
            handle.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer);
            store.PublishMetrics();
        }

        applyFailure?.Throw();
    }

    /// <summary>As <c>PreimageRocksdbPersistence</c>, which has no raw access.</summary>
    private void ThrowIfRawUnavailable()
    {
        if (store.IsPreimage) throw new InvalidOperationException("Raw operations not available in preimage mode");
    }

    /// <summary>As <c>PreimageRocksdbPersistence</c>, which cannot be snap synced.</summary>
    private void ThrowIfSnapSyncUnavailable()
    {
        if (store.IsPreimage) throw new NotSupportedException("Snap sync not supported in preimage mode");
    }

    /// <summary>Reserves room for an operation of <paramref name="size"/> bytes after its tag, handing over the buffer first if it is full.</summary>
    private OpWriter Append(byte tag, int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int needed = 1 + size;
        if (_length + needed > _buffer.Length)
        {
            HandOver();
            if (needed > _buffer.Length)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = ArrayPool<byte>.Shared.Rent(needed);
            }
        }

        Span<byte> op = _buffer.AsSpan(_length, needed);
        _length += needed;
        op[0] = tag;
        return new OpWriter(op[1..]);
    }

    /// <summary>Applies the buffered operations to the native batch.</summary>
    private void HandOver()
    {
        if (_length == 0) return;

        int length = _length;
        _length = 0;
        fixed (byte* ops = _buffer)
        {
            store.ThrowIfFailed(NativeMethods.fdb_batch_apply(handle, ops, (nuint)length));
        }
    }

    /// <summary>Writes the fields of one operation.</summary>
    private ref struct OpWriter(Span<byte> span)
    {
        private Span<byte> _rest = span;

        public void Write(scoped ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_rest);
            _rest = _rest[bytes.Length..];
        }

        public void WriteWithLength(scoped ReadOnlySpan<byte> bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_rest, (uint)bytes.Length);
            _rest = _rest[LengthSize..];
            Write(bytes);
        }

        public void Write(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_rest, value);
            _rest = _rest[sizeof(ulong)..];
        }

        public void Write(in UInt256 value)
        {
            Write(value.u0);
            Write(value.u1);
            Write(value.u2);
            Write(value.u3);
        }

        public void Write(Account account)
        {
            Write(account.Nonce);
            Write(account.Balance);
            Write(account.StorageRoot.Bytes);
            Write(account.CodeHash.Bytes);
        }

        public void Write(in TreePath path)
        {
            Write(path.Path.Bytes);
            _rest[0] = (byte)path.Length;
            _rest = _rest[1..];
        }
    }
}
