// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;

namespace Nethermind.Serialization.Rlp.Test;

public class RlpReaderTest
{
    [Test]
    public void ReadShortString()
    {
        byte[] source = [0x83, (byte)'d', (byte)'o', (byte)'g'];

        var reader = new RlpReader(source);
        string actual = reader.ReadString();

        actual.Should().Be("dog");
    }

    [Test]
    public void ReadEmptyString()
    {
        byte[] source = [0x80];

        var reader = new RlpReader(source);
        string actual = reader.ReadString();

        actual.Should().Be("");
    }

    [Test]
    public void ReadLongString()
    {
        byte[] source = [0xb8, 0x38, .."Lorem ipsum dolor sit amet, consectetur adipisicing elit"u8];

        var reader = new RlpReader(source);
        string actual = reader.ReadString();

        actual.Should().Be("Lorem ipsum dolor sit amet, consectetur adipisicing elit");
    }

    [Test]
    public void ReadShortInteger()
    {
        for (int i = 0; i < 0x80; i++)
        {
            var integer = i;
            byte[] source = [(byte)integer];

            var reader = new RlpReader(source);
            int actual = reader.ReadInt32();

            actual.Should().Be(integer);
        }
    }

    [Test]
    public void ReadLongInteger()
    {
        for (int i = 0x100; i < 0xFFFF; i++)
        {
            var integer = i;
            byte[] source = [0x82, (byte)((integer & 0xFF00) >> 8), (byte)((integer & 0x00FF) >> 0)];

            var reader = new RlpReader(source);
            int actual = reader.ReadInt32();

            actual.Should().Be(integer);
        }
    }

    [Test]
    public void ReadStringList()
    {
        byte[] source = [0xc8, 0x83, .."cat"u8, 0x83, .."dog"u8];

        var reader = new RlpReader(source);
        var actual = reader.ReadList(static (ref RlpReader r) =>
        {
            var cat = r.ReadString();
            var dog = r.ReadString();

            return (cat, dog);
        });

        actual.Should().Be(("cat", "dog"));
    }

    [Test]
    public void ReadSetTheoreticalRepresentation()
    {
        byte[] source = [0xc7, 0xc0, 0xc1, 0xc0, 0xc3, 0xc0, 0xc1, 0xc0];

        var reader = new RlpReader(source);
        object[] actual = reader.ReadList(static (ref RlpReader r) =>
        {
            var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
            var _2 = r.ReadList(static (ref RlpReader r) =>
            {
                var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
                return new object[] { _1 };
            });
            var _3 = r.ReadList(static (ref RlpReader r) =>
            {
                var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
                var _2 = r.ReadList(static (ref RlpReader r) =>
                {
                    var _1 = r.ReadList(static (ref RlpReader _) => Array.Empty<object>());
                    return new object[] { _1 };
                });

                return new object[] { _1, _2 };
            });

            return new object[] { _1, _2, _3 };
        });

        actual.Should().BeEquivalentTo(new object[]
        {
            new object[] { },
            new object[] { new object[] { } },
            new object[] { new object[] { }, new object[] { new object[] { } } },
        });
    }

    [Test]
    public void ReadEmptyList()
    {
        byte[] source = [0xc0];

        var reader = new RlpReader(source);
        object[] actual = reader.ReadList((ref RlpReader _) => Array.Empty<object>());

        actual.Should().BeEmpty();
    }
}

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
            // Not an Object
            throw new Exception();
        }

        return result;
    }

    public delegate TResult ReadListFunc<out TResult>(ref RlpReader arg);

    public T ReadList<T>(ReadListFunc<T> func)
    {
        T result;
        var header = _buffer[_position];
        if (header < 0xC0)
        {
            // Not a List
            throw new Exception();
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
            throw new NotImplementedException();
        }

        return result;
    }
}

public static class IntRlpReader
{
    public static Int32 ReadInt32(this ref RlpReader reader)
    {
        ReadOnlySpan<byte> obj = reader.ReadObject();
        return Int32Primitive.Read(obj);
    }
}

public static class StringRlpReader
{
    public static string ReadString(this ref RlpReader reader)
    {
        ReadOnlySpan<byte> obj = reader.ReadObject();
        return Encoding.UTF8.GetString(obj);
    }
}

public static class Int32Primitive
{
    /// <summary>
    /// Reads a <see cref="int" /> from the beginning of a read-only span of bytes, as big endian.
    /// </summary>
    /// <param name="source">The read-only span to read.</param>
    /// <returns>The big endian value.</returns>
    /// <remarks>The span is padded with leading `0`s as needed.</remarks>
    public static int Read(ReadOnlySpan<byte> source)
    {
        Span<byte> buffer = stackalloc byte[sizeof(Int32)];
        source.CopyTo(buffer[^source.Length..]);
        return BinaryPrimitives.ReadInt32BigEndian(buffer);
    }
}
