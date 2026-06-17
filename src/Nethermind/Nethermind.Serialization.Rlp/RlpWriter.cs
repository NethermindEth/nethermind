// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

public ref struct RlpWriter(Span<byte> data) : IRlpWriteBackend
{
    private Span<byte> _data = data;
    private int _position;

    public RlpWriter(byte[]? data)
        : this((data ?? Array.Empty<byte>()).AsSpan())
    {
    }

    public RlpWriter(in CappedArray<byte> data)
        : this((data.IsNotNull ? data : CappedArray<byte>.Empty).AsSpan())
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

    public override readonly string ToString() => $"[{nameof(RlpWriter)}|{_position}/{Length}]";
}

public ref struct PooledRlpWriter(int length) : IRlpWriteBackend, IDisposable
{
    private ArrayPoolSpan<byte> _buffer = new(length);
    private int _position;
    private bool _ownsBuffer = true;

    public Span<byte> Data
    {
        get
        {
            ThrowIfNotOwned();
            return _buffer.Slice(0, _buffer.Length);
        }
    }

    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ThrowIfNotOwned();
            return _buffer.Slice(0, _position);
        }
    }

    public int Position
    {
        readonly get => _position;
        set => _position = value;
    }

    public int Length
    {
        get
        {
            ThrowIfNotOwned();
            return _buffer.Length;
        }
    }

    void IRlpWriteBackend.WriteByte(byte byteToWrite)
    {
        ThrowIfNotOwned();
        _buffer[_position++] = byteToWrite;
    }

    void IRlpWriteBackend.Write(scoped ReadOnlySpan<byte> bytesToWrite)
    {
        ThrowIfNotOwned();
        bytesToWrite.CopyTo(_buffer.Slice(_position, bytesToWrite.Length));
        _position += bytesToWrite.Length;
    }

    void IRlpWriteBackend.WriteZero(int lengthToWrite)
    {
        ThrowIfNotOwned();
        _buffer.Slice(_position, lengthToWrite).Clear();
        _position += lengthToWrite;
    }

    public void Reset()
    {
        ThrowIfNotOwned();
        _position = 0;
    }

    public ArrayPoolSpan<byte> DetachBuffer()
    {
        ThrowIfNotOwned();
        _ownsBuffer = false;
        return _buffer;
    }

    public void Dispose()
    {
        if (_ownsBuffer)
        {
            _buffer.Dispose();
            _ownsBuffer = false;
            _position = 0;
        }
    }

    private readonly void ThrowIfNotOwned()
    {
        if (!_ownsBuffer)
        {
            throw new ObjectDisposedException(nameof(PooledRlpWriter));
        }
    }

    public override readonly string ToString()
        => _ownsBuffer
            ? $"[{nameof(PooledRlpWriter)}|{_position}/{_buffer.Length}]"
            : $"[{nameof(PooledRlpWriter)}|detached]";
}

public struct ByteBufferRlpWriter(IByteBuffer byteBuffer) : IRlpWriteBackend
{
    private readonly IByteBuffer _byteBuffer = byteBuffer ?? throw new ArgumentNullException(nameof(byteBuffer));
    private int _position;

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

    public override readonly string ToString() => $"[{nameof(ByteBufferRlpWriter)}|{_byteBuffer.GetType().Name}|{_position}]";
}

public struct KeccakRlpWriter(KeccakHash keccakHash) : IRlpWriteBackend
{
    private readonly KeccakHash _keccakHash = keccakHash ?? throw new ArgumentNullException(nameof(keccakHash));
    private int _position;

    public KeccakRlpWriter()
        : this(KeccakHash.Create())
    {
    }

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

    public override readonly string ToString() => $"[{nameof(KeccakRlpWriter)}|{_keccakHash.GetType().Name}|{_position}]";
}
