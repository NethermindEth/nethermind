// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Nethermind.Serialization.FluentRlp;

public delegate TResult RefRlpReaderFunc<out TResult>(scoped ref RlpReader arg) where TResult : allows ref struct;

public delegate void RefRlpReaderAction(ref RlpReader arg);

public class RlpReaderException(string message) : Exception(message);

public ref struct RlpReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public RlpReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public bool HasNext => _position < _buffer.Length;

    public T ReadInteger<T>() where T : IBinaryInteger<T>, ISignedNumber<T>
    {
        ReadOnlySpan<byte> bigEndian;
        var header = _buffer[_position];
        if (header < 0x80)
        {
            bigEndian = _buffer.Slice(_position++, 1);
        }
        else
        {
            bigEndian = ReadBytes();
        }

        Span<byte> buffer = stackalloc byte[Marshal.SizeOf<T>()];
        bigEndian.CopyTo(buffer[^bigEndian.Length..]);
        return T.ReadBigEndian(buffer, false);
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        ReadOnlySpan<byte> result;
        var header = _buffer[_position];
        if (header < 0x80 || header >= 0xC0)
        {
            throw new RlpReaderException("RLP does not correspond to a byte string");
        }

        if (header < 0xB8)
        {
            header -= 0x80;
            result = _buffer.Slice(++_position, header);
            _position += header;
        }
        else
        {
            header -= 0xB7;
            ReadOnlySpan<byte> binaryLength = _buffer.Slice(++_position, header);
            _position += header;
            var length = Int32Primitive.Read(binaryLength);
            result = _buffer.Slice(_position, length);
            _position += length;
        }

        return result;
    }

    public T ReadSequence<T>(RefRlpReaderFunc<T> func)
    {
        T result;
        var header = _buffer[_position++];
        if (header < 0xC0)
        {
            throw new RlpReaderException("RLP does not correspond to a sequence");
        }

        if (header < 0xF8)
        {
            var length = header - 0xC0;
            var reader = new RlpReader(_buffer.Slice(_position, length));
            result = func(ref reader);
            _position += length;
        }
        else
        {
            var lengthOfLength = header - 0xF7;
            ReadOnlySpan<byte> binaryLength = _buffer.Slice(_position, lengthOfLength);
            _position += lengthOfLength;
            int length = Int32Primitive.Read(binaryLength);
            var reader = new RlpReader(_buffer.Slice(_position, length));
            result = func(ref reader);
            _position += length;
        }

        return result;
    }

    public T Choice<T>(params ReadOnlySpan<RefRlpReaderFunc<T>> alternatives)
    {
        int startingPosition = _position;
        foreach (var f in alternatives)
        {
            try
            {
                return f(ref this);
            }
            catch (Exception)
            {
                _position = startingPosition;
            }
        }
        throw new RlpReaderException("RLP does not correspond to any alternative");
    }
}
