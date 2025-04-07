// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Nethermind.Optimism.CL.Decoding;

public static class FrameDecoder
{
    // TODO: Evaluate if a custom `Enumerator` would be worth it
    public static IEnumerable<Frame> DecodeFrames(Memory<byte> source)
    {
        while (source.Length != 0)
        {
            byte version = source.Span[0];
            switch (version)
            {
                case 0:
                    int bytesRead = Frame.FromBytes(source.Span[1..], out Frame frame);
                    yield return frame;
                    source = source[(1 + bytesRead)..];
                    break;
                default:
                    throw new Exception($"Frame Decoder version {version} is not supported.");
            }
        }
    }
}

/// <remarks>
/// https://specs.optimism.io/protocol/derivation.html#frame-format
/// </remarks>
public readonly struct Frame : IEquatable<Frame>
{
    public UInt128 ChannelId { get; init; }
    public UInt16 FrameNumber { get; init; }
    public byte[] FrameData { get; init; }
    public bool IsLast { get; init; }

    public int Size
    {
        get
        {
            unsafe
            {
                return sizeof(UInt128) + sizeof(UInt16) + sizeof(UInt32) + FrameData.Length + sizeof(byte);
            }
        }
    }

    public static int FromBytes(ReadOnlySpan<byte> buffer, out Frame frame)
    {
        unsafe
        {
            UInt128 channelId = BinaryPrimitives.ReadUInt128BigEndian(buffer.TakeAndMove(sizeof(UInt128)));
            UInt16 frameNumber = BinaryPrimitives.ReadUInt16BigEndian(buffer.TakeAndMove(sizeof(UInt16)));
            UInt32 frameDataLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.TakeAndMove(sizeof(UInt32)));
            ReadOnlySpan<byte> frameData = buffer.TakeAndMove((int)frameDataLength);
            byte isLast = buffer[0];
            if (isLast != 0 && isLast != 1)
            {
                throw new FormatException($"Invalid {nameof(IsLast)} flag");
            }

            frame = new Frame
            {
                ChannelId = channelId,
                FrameNumber = frameNumber,
                FrameData = frameData.ToArray(),
                IsLast = isLast == 1
            };

            return frame.Size;
        }
    }

    public int WriteTo(Span<byte> span)
    {
        int initialLength = span.Length;

        unsafe
        {
            BinaryPrimitives.WriteUInt128BigEndian(span.TakeAndMove(sizeof(UInt128)), ChannelId);
            BinaryPrimitives.WriteUInt16BigEndian(span.TakeAndMove(sizeof(UInt16)), FrameNumber);
            BinaryPrimitives.WriteUInt32BigEndian(span.TakeAndMove(sizeof(UInt32)), (UInt32)FrameData.Length);
            FrameData.CopyTo(span.TakeAndMove(FrameData.Length));
            span.TakeAndMove(1)[0] = (byte)(IsLast ? 1 : 0);
        }

        return initialLength - span.Length;
    }

    public bool Equals(Frame other) =>
        ChannelId.Equals(other.ChannelId) &&
        FrameNumber == other.FrameNumber &&
        FrameData.SequenceEqual(other.FrameData) &&
        IsLast == other.IsLast;

    public override bool Equals(object? obj) => obj is Frame other && Equals(other);

    public static bool operator ==(Frame left, Frame right) => left.Equals(right);

    public static bool operator !=(Frame left, Frame right) => !(left == right);

    public override int GetHashCode() => HashCode.Combine(ChannelId, FrameNumber, FrameData, IsLast);
}
