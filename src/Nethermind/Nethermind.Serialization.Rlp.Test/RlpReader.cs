// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp.Test;

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

    public ReadOnlySpan<byte> ReadObject()
    {
        ReadOnlySpan<byte> result;
        var header = _buffer[_position];
        if (header < 0x80)
        {
            result = _buffer.Slice(_position++, 1);
        }
        else if (header < 0xB8)
        {
            header -= 0x80;
            result = _buffer.Slice(++_position, header);
            _position += header;
        }
        else if (header < 0xC0)
        {
            header -= 0xB7;
            ReadOnlySpan<byte> binaryLength = _buffer.Slice(++_position, header);
            _position += header;
            var length = Int32Primitive.Read(binaryLength);
            result = _buffer.Slice(_position, length);
            _position += length;
        }
        else
        {
            throw new RlpReaderException("RLP does not correspond to an object");
        }

        return result;
    }

    public T ReadList<T>(RefRlpReaderFunc<T> func)
    {
        T result;
        var header = _buffer[_position];
        if (header < 0xC0)
        {
            throw new RlpReaderException("RLP does not correspond to a list");
        }

        if (header < 0xF8)
        {
            _position += 1;
            var length = header - 0xC0;
            var reader = new RlpReader(_buffer.Slice(_position, length));
            result = func(ref reader);
            _position += length;
        }
        else
        {
            _position += 1;
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

    public void ReadList(RefRlpReaderAction func)
    {
        ReadList<object?>((scoped ref RlpReader r) =>
        {
            func(ref r);
            return null;
        });
    }

    public bool HasNext => _position < _buffer.Length;
}
