// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip2930;
using Nethermind.Serialization.Rlp.RlpWriter;

namespace Nethermind.Serialization.Rlp.RlpReader;

public sealed class RlpStreamReader
{
    private readonly RlpStream _stream;
    private readonly int _endPosition;

    public RlpStreamReader(RlpStream stream)
    {
        _stream = stream;
        _endPosition = stream.Length;
    }

    private RlpStreamReader(RlpStream stream, int endPosition)
    {
        _stream = stream;
        _endPosition = endPosition;
    }

    public bool HasRemainder => _stream.Position < _endPosition;

    public ReadOnlySpan<byte> ReadSequence(bool strict, Action<RlpStreamReader> action)
    {
        (int _, int contentLength) = _stream.ReadPrefixAndContentLength();
        return ReadByteBlock(strict, contentLength, action);
    }

    public ReadOnlySpan<byte> ReadSequence(Action<RlpStreamReader> action) => ReadSequence(strict: true, action);

    public ReadOnlySpan<byte> ReadUntilEnd(bool strict, Action<RlpStreamReader> action)
    {
        int length = _stream.Length;
        return ReadByteBlock(strict, length, action);
    }

    private ReadOnlySpan<byte> ReadByteBlock(bool strict, int bytes, Action<RlpStreamReader> action)
    {
        var startingPosition = _stream.Position;
        var expectedEndPosition = startingPosition + bytes;
        var sequence = _stream.Peek(bytes);

        action(new RlpStreamReader(_stream, endPosition: expectedEndPosition));

        if (strict && _stream.Position != expectedEndPosition)
        {
            throw new RlpException("Sequence length mismatch");
        }

        return sequence;
    }

    public T Read<T>(IRlpStreamDecoder<T> decoder, RlpBehaviors rlpBehaviors) => decoder.Decode(_stream, rlpBehaviors);
    public Address ReadAddress() => _stream.DecodeAddress();
    public byte ReadByte() => _stream.DecodeByte();
    public Memory<byte> ReadByteArray() => _stream.DecodeByteArray();
    public ReadOnlySpan<byte> ReadByteArraySpan() => _stream.DecodeByteArraySpan();
    public long ReadLong() => _stream.DecodeLong();
    public UInt256 ReadUInt256() => _stream.DecodeUInt256();
    public ulong ReadULong() => _stream.DecodeULong();
}
