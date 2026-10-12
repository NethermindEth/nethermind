// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>
/// Writes the fields of one entry of the packed streams of native/crates/flatdb-ffi/src/op_stream.rs: write batch
/// operations and snapshot records.
/// </summary>
internal ref struct OpFieldWriter(Span<byte> span)
{
    public const int AddressSize = Address.Size;
    public const int HashSize = ValueHash256.MemorySize;
    public const int UInt256Size = 32;
    public const int AccountSize = sizeof(ulong) + UInt256Size + 2 * HashSize;
    public const int PathSize = HashSize + 1;
    public const int LengthSize = sizeof(uint);

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

    public void Write(byte value)
    {
        _rest[0] = value;
        _rest = _rest[1..];
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
        Write((byte)path.Length);
    }
}
