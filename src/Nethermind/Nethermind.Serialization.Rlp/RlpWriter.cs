// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

public ref struct RlpWriter : IRlpWriteBackend
{
    private Span<byte> _data;
    private int _position;

    public RlpWriter(Span<byte> data)
    {
        _data = data;
        _position = 0;
    }

    public RlpWriter(byte[]? data)
        : this((data ?? Array.Empty<byte>()).AsSpan())
    {
    }

    public RlpWriter(in CappedArray<byte> data)
        : this((data.IsNotNull ? data : CappedArray<byte>.Empty).AsSpan())
    {
    }

    public RlpWriter(int length)
        : this(new byte[length])
    {
    }

    public readonly Span<byte> Data => _data;

    public readonly ReadOnlySpan<byte> WrittenSpan => _data[.._position];

    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    public readonly int Length => _data.Length;

    void IRlpWriteBackend.WriteByte(byte byteToWrite) => _data[_position++] = byteToWrite;

    void IRlpWriteBackend.Write(scoped ReadOnlySpan<byte> bytesToWrite)
    {
        bytesToWrite.CopyTo(_data.Slice(_position, bytesToWrite.Length));
        _position += bytesToWrite.Length;
    }

    void IRlpWriteBackend.WriteZero(int length)
    {
        _data.Slice(_position, length).Clear();
        _position += length;
    }

    public void Reset() => _position = 0;

    void IDisposable.Dispose()
    {
    }

    public override readonly string ToString() => $"[{nameof(RlpWriter)}|{_position}/{Length}]";
}

public struct ByteBufferRlpWriter : IRlpWriteBackend
{
    private readonly IByteBuffer _byteBuffer;
    private int _position;

    public ByteBufferRlpWriter(IByteBuffer byteBuffer)
    {
        ArgumentNullException.ThrowIfNull(byteBuffer);

        _byteBuffer = byteBuffer;
        _position = 0;
    }

    public int Position
    {
        readonly get => _position;
        set => throw new InvalidOperationException("ByteBuffer-backed writer position cannot be reassigned.");
    }

    void IRlpWriteBackend.WriteByte(byte byteToWrite)
    {
        _byteBuffer.EnsureWritable(1);
        _byteBuffer.WriteByte(byteToWrite);
        _position++;
    }

    void IRlpWriteBackend.Write(scoped ReadOnlySpan<byte> bytesToWrite)
    {
        _byteBuffer.EnsureWritable(bytesToWrite.Length);
        if (_byteBuffer.HasArray)
        {
            Span<byte> target = _byteBuffer.Array.AsSpan(_byteBuffer.ArrayOffset + _byteBuffer.WriterIndex, bytesToWrite.Length);
            bytesToWrite.CopyTo(target);
            _byteBuffer.SetWriterIndex(_byteBuffer.WriterIndex + bytesToWrite.Length);
        }
        else
        {
            for (int i = 0; i < bytesToWrite.Length; i++)
            {
                _byteBuffer.WriteByte(bytesToWrite[i]);
            }
        }

        _position += bytesToWrite.Length;
    }

    void IRlpWriteBackend.WriteZero(int length)
    {
        _byteBuffer.EnsureWritable(length);
        _byteBuffer.WriteZero(length);
        _position += length;
    }

    readonly void IDisposable.Dispose()
    {
    }

    public override readonly string ToString() => $"[{nameof(ByteBufferRlpWriter)}|{_byteBuffer.GetType().Name}|{_position}]";
}

public struct KeccakRlpWriter : IRlpWriteBackend
{
    private readonly KeccakHash _keccakHash;
    private int _position;

    public KeccakRlpWriter(KeccakHash keccakHash)
    {
        ArgumentNullException.ThrowIfNull(keccakHash);

        _keccakHash = keccakHash;
        _position = 0;
    }

    public static KeccakRlpWriter Create() => new(KeccakHash.Create());

    public readonly Hash256 GetHash() => new(_keccakHash.GenerateValueHash());

    public readonly ValueHash256 GetValueHash() => _keccakHash.GenerateValueHash();

    public int Position
    {
        readonly get => _position;
        set => throw new InvalidOperationException("Keccak-backed writer position cannot be reassigned.");
    }

    void IRlpWriteBackend.WriteByte(byte byteToWrite)
    {
        _keccakHash.Update(MemoryMarshal.CreateSpan(ref byteToWrite, 1));
        _position++;
    }

    void IRlpWriteBackend.Write(scoped ReadOnlySpan<byte> bytesToWrite)
    {
        _keccakHash.Update(bytesToWrite);
        _position += bytesToWrite.Length;
    }

    void IRlpWriteBackend.WriteZero(int length)
    {
        int originalLength = length;
        Span<byte> zeros = stackalloc byte[Math.Min(length, 256)];
        zeros.Clear();
        while (length > 0)
        {
            int chunkLength = Math.Min(length, zeros.Length);
            _keccakHash.Update(zeros[..chunkLength]);
            length -= chunkLength;
        }

        _position += originalLength;
    }

    readonly void IDisposable.Dispose()
    {
    }

    public override readonly string ToString() => $"[{nameof(KeccakRlpWriter)}|{_keccakHash.GetType().Name}|{_position}]";
}
