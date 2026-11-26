// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.Rlpx
{
    internal static class FrameHeaderReader
    {
        private const int HeaderBodyOffset = 3;
        private const int HeaderBodyLength = Frame.HeaderSize - HeaderBodyOffset;
        private const byte ShortListMaxPrefix = 0xf7;

        /// <summary>Reads a decrypted frame header.</summary>
        /// <exception cref="InvalidFrameException">The header is not well formed, or its sizes are out of range.</exception>
        public static FrameInfo ReadFrameHeader(ReadOnlySpan<byte> header)
        {
            int frameSize = header[0] & 0xFF;
            frameSize = (frameSize << 8) + (header[1] & 0xFF);
            frameSize = (frameSize << 8) + (header[2] & 0xFF);

            ReadHeaderBody(header.Slice(HeaderBodyOffset, HeaderBodyLength), out int? contextId, out int? totalPacketSize);

            ValidateTotalPacketSize(frameSize, totalPacketSize);
            return new FrameInfo(frameSize, contextId, totalPacketSize);
        }

        /// <summary>Decodes the RLP header body of a frame header.</summary>
        /// <remarks>
        /// Kept separate from <see cref="ReadFrameHeader"/> so that the exception handling region covers the RLP
        /// decoding only, rather than also spanning the frame size read and <see cref="ValidateTotalPacketSize"/>.
        /// </remarks>
        /// <exception cref="InvalidFrameException">The header body is not a well formed RLP sequence.</exception>
        private static void ReadHeaderBody(ReadOnlySpan<byte> body, out int? contextId, out int? totalPacketSize)
        {
            try
            {
                // The header body is shorter than 56 bytes, so its sequence prefix holds the length.
                if (body[0] is >= Rlp.EmptyListByte and <= ShortListMaxPrefix && body[0] - Rlp.EmptyListByte > body.Length - 1)
                {
                    throw new InvalidFrameException($"Invalid Rlpx header lengths, header body RLP length {body[0] - Rlp.EmptyListByte} exceeds the {body.Length - 1} bytes left in the header");
                }

                RlpReader headerBody = new(body);
                RlpReader headerBodyItems = headerBody.EnterSequence(out _);
                int numberOfItems = headerBodyItems.CountItems();
                headerBodyItems.DecodeInt(); // not needed - adaptive IDs - DO NOT COMMENT OUT!!! - decode takes int of the RLP sequence and moves the position
                contextId = numberOfItems > 1 ? headerBodyItems.DecodeInt() : (int?)null;
                totalPacketSize = numberOfItems > 2 ? headerBodyItems.DecodeInt() : (int?)null;
                headerBodyItems.CheckEnd();
            }
            catch (Exception exception) when (exception is RlpReaderException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                throw new InvalidFrameException(exception);
            }
        }

        private static void ValidateTotalPacketSize(int frameSize, int? totalPacketSize)
        {
            if (totalPacketSize is not null)
            {
                if (totalPacketSize <= 0 || totalPacketSize > SnappyParameters.MaxSnappyLength)
                {
                    ThrowInvalidFrameException(frameSize, totalPacketSize.Value);
                }

                if (frameSize > totalPacketSize)
                {
                    ThrowInvalidFrameException(frameSize, totalPacketSize.Value);
                }
            }

            [DoesNotReturn, StackTraceHidden]
            static void ThrowInvalidFrameException(int frameSize, int totalPacketSize) => throw new InvalidFrameException($"Invalid Rlpx header lengths, packet size {totalPacketSize}, frame size {frameSize}");
        }

        internal readonly struct FrameInfo(int size, int? contextId, int? totalPacketSize)
        {
            public int Size { get; } = size;
            public int? ContextId { get; } = contextId;
            public int? TotalPacketSize { get; } = totalPacketSize;
            public int Padding => Frame.CalculatePadding(Size);

            public int PayloadSize => Size + Padding;
        }
    }
}
