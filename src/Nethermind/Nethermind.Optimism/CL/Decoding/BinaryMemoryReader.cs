// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Decoding;

/// <summary>
/// A wrapper over <see cref="ReadOnlyMemory{T}"/> used as byref where <c>T</c> is <see cref="byte"/>.
/// Methods are provided to <see cref="Peek"/>, <see cref="Skip"/> and <see cref="Take"/> bytes, with overloads for single <see cref="byte"/>
/// </summary>
/// <param name="memory">The underlying memory to be read</param>
public class BinaryMemoryReader(ReadOnlyMemory<byte> memory)
{
    private int _offset;

    public ReadOnlyMemory<byte> Peek(int bytes)
    {
        return memory[_offset..(_offset + bytes)];
    }

    public void Skip(int bytes)
    {
        _offset += bytes;
    }

    public ReadOnlyMemory<byte> Take(int bytes)
    {

        var m = Peek(bytes);
        Skip(bytes);
        return m;
    }

    public byte PeekByte()
    {
        return memory.Span[_offset];
    }

    public void SkipByte()
    {
        _offset++;
    }

    public byte TakeByte()
    {
        var b = PeekByte();
        SkipByte();
        return b;
    }

    public ReadOnlyMemory<byte> Remainder => memory[_offset..];

    public bool HasRemainder => _offset < memory.Length;

    public TResult Read<TResult>(Func<BinaryMemoryReader, TResult> parser)
    {
        return parser(this);
    }

    public delegate bool TryParseFunc<TResult>(ReadOnlySpan<byte> source, out TResult result);
    public TResult Read<TResult>(TryParseFunc<TResult> parser, int bytes)
    {
        bool success = parser(memory[_offset..].Span, out TResult result);
        if (!success) throw new FormatException();

        _offset += bytes;
        return result;
    }

    public TResult Read<TResult>(Func<ReadOnlySpan<byte>, TResult> parser) where TResult : unmanaged
    {
        unsafe
        {
            TResult result = parser(memory[_offset..].Span);
            _offset += sizeof(TResult);
            return result;
        }
    }

    public TResult Read<TResult, TArg>(Func<ReadOnlySpan<byte>, TArg, (TResult, int)> parser, TArg arg)
    {
        (TResult result, int read) = parser(memory[_offset..].Span, arg);
        if (read < 0) throw new FormatException();

        _offset += read;
        return result;
    }
}

public static class TxParser
{
    public static (ReadOnlyMemory<byte>, TxType) Data(BinaryMemoryReader reader)
    {
        byte firstByte = reader.PeekByte();
        TxType type;
        if (firstByte <= 0x7F)
        {
            type = (TxType)firstByte;
            reader.Skip(1);
        }
        else
        {
            type = TxType.Legacy;
        }

        Rlp.ValueDecoderContext decoder = new(reader.Remainder.Span);
        if (!decoder.IsSequenceNext())
        {
            throw new FormatException("Invalid tx data");
        }

        int n = decoder.PeekNextRlpLength();
        return (reader.Take(n), type);
    }
}

public static class Protobuf
{
    public static ulong DecodeULong(BinaryMemoryReader reader)
    {
        ulong value = 0;
        int shift = 0;
        bool more = true;

        while (more)
        {
            byte b = reader.TakeByte();
            more = (b & 0x80) != 0;   // extract msb
            ulong chunk = b & 0x7fUL; // extract lower 7 bits
            value |= chunk << shift;
            shift += 7;
        }

        return value;
    }

    public static (BigInteger, int) DecodeBitList(ReadOnlySpan<byte> source, ulong bitLength)
    {
        ulong length = bitLength / 8;
        if (bitLength % 8 != 0)
        {
            length++;
        }

        BigInteger x = new(source[..(int)length], true, true);
        if (x.GetBitLength() > (long)bitLength)
        {
            throw new FormatException("Invalid bit length");
        }

        return (x, (int)length);
    }
}
