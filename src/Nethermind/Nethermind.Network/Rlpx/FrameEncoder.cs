// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Rlpx;

/// <summary>Encodes packets into RLPx frames.</summary>
/// <remarks>
/// A packet is its RLP packet type followed by its content. The encoder writes the frames into memory the caller provides,
/// so each transport keeps its own buffers.
/// </remarks>
internal sealed class FrameEncoder(IFrameCipher? cipher, IFrameMacProcessor? macProcessor)
{
    internal enum EncodingMode
    {
        /// <summary>Splits packets over <see cref="Frame.DefaultMaxFrameSize"/> into chunked frames with a context id.</summary>
        Framed,

        /// <summary>Puts each packet into a single frame; kept for wire-equivalence tests and outbound benchmarks.</summary>
        Unframed,

        /// <summary>Puts each packet into a single frame with its content compressed.</summary>
        Snappy
    }

    private int _contextId;
    private ILogger _snappyLogger;
    private int _encodingMode = (int)EncodingMode.Framed;

    /// <summary>Gets the mode the next packet is encoded in.</summary>
    /// <remarks>
    /// Read it once per packet and pass it to both <see cref="GetMaxEncodedLength"/> and <see cref="Encode"/>: Snappy can be
    /// enabled from another thread between the two calls, and a destination sized for framing may be too small for Snappy.
    /// </remarks>
    public EncodingMode Mode => (EncodingMode)Volatile.Read(ref _encodingMode);

    public void DisableFraming() => Interlocked.CompareExchange(ref _encodingMode, (int)EncodingMode.Unframed, (int)EncodingMode.Framed);

    public void EnableSnappy(ILogger logger)
    {
        _snappyLogger = logger;
        Volatile.Write(ref _encodingMode, (int)EncodingMode.Snappy);
    }

    /// <summary>Gets an upper bound of the encoded size, so the destination is not grown and copied while encoding.</summary>
    public int GetMaxEncodedLength(int packetLength, EncodingMode mode)
    {
        int macsSize = cipher is null ? 0 : 2 * Frame.MacSize;
        if (mode == EncodingMode.Snappy)
        {
            return Frame.HeaderSize + macsSize + Snappier.Snappy.GetMaxCompressedLength(packetLength) + Frame.BlockSize;
        }

        int framesCount = mode == EncodingMode.Framed ? (packetLength - 1) / Frame.DefaultMaxFrameSize + 1 : 1;
        return packetLength + framesCount * (Frame.HeaderSize + macsSize) + Frame.BlockSize;
    }

    /// <summary>Writes the frames of <paramref name="packet"/> into <paramref name="destination"/>.</summary>
    /// <param name="destination">At least <see cref="GetMaxEncodedLength"/> bytes for the same mode.</param>
    /// <returns>The number of bytes written.</returns>
    public int Encode(ReadOnlySpan<byte> packet, Span<byte> destination, EncodingMode mode)
    {
        if (mode == EncodingMode.Snappy)
        {
            return EncodeCompressed(packet, destination);
        }

        int contextId = Interlocked.Increment(ref _contextId);

        int totalPayloadSize = packet.Length;
        int maxFrameSize = mode == EncodingMode.Framed ? Frame.DefaultMaxFrameSize : int.MaxValue;

        int framesCount = (totalPayloadSize - 1) / maxFrameSize + 1;
        int position = 0;
        for (int i = 0; i < framesCount; i++)
        {
            int totalPayloadOffset = maxFrameSize * i;
            int framePayloadSize = Math.Min(maxFrameSize, totalPayloadSize - totalPayloadOffset);
            int paddingSize = i == framesCount - 1 ? Frame.CalculatePadding(totalPayloadSize) : 0;
            Span<byte> frame = destination[position..];

            // 000 - 016 | header
            // 016 - 01x | packet type
            // 01x - frm | payload
            // frm - %16 | padding to 16

            // here we encode payload size as an RLP encoded long value without leading zeros
            frame[0] = (byte)(framePayloadSize >> 16);
            frame[1] = (byte)(framePayloadSize >> 8);
            frame[2] = (byte)framePayloadSize;

            if (framesCount == 1)
            {
                // // commented out after Trinity reported #2052
                // // not 100% sure they are right but they may be
                // // 193|128 is an RLP encoded array with one element that is zero
                // /*3*/
                // output.WriteByte(193);
                // /*4*/
                // output.WriteByte(128);
                // /*5-16*/
                // output.WriteZero(11);

                // 194|128 is an RLP encoded array with two elements that are zero
                frame[3] = 194;
                frame[4] = 128;
                frame[5] = 128;
                frame[6..Frame.HeaderSize].Clear();
            }
            else
            {
                int contentLength = Rlp.LengthOf(contextId) + Rlp.LengthOf(0);
                if (i == 0)
                {
                    contentLength += Rlp.LengthOf(totalPayloadSize);
                }
                int headerDataLength = Rlp.LengthOfSequence(contentLength);
                RlpWriter writer = RlpWriter.ContentWriter(frame.Slice(3, headerDataLength));
                RlpSequence sequence = writer.StartSequence(contentLength);
                writer.Encode(0);
                writer.Encode(contextId);
                if (i == 0)
                {
                    writer.Encode(totalPayloadSize);
                }
                writer.EndSequence(sequence);
                Rlp.CheckLength(in writer, headerDataLength);
                frame[(3 + headerDataLength)..Frame.HeaderSize].Clear();
            }

            int payloadStart = Frame.HeaderSize + (cipher is null ? 0 : Frame.MacSize);
            /*message*/
            packet.Slice(totalPayloadOffset, framePayloadSize).CopyTo(frame[payloadStart..]);
            /*padding to 16*/
            frame.Slice(payloadStart + framePayloadSize, paddingSize).Clear();

            position += payloadStart + framePayloadSize + paddingSize;
            if (cipher is not null)
            {
                EncryptFrame(frame, framePayloadSize + paddingSize);
                position += Frame.MacSize;
            }
        }

        return position;
    }

    private int EncodeCompressed(ReadOnlySpan<byte> packet, Span<byte> destination)
    {
        int payloadStart = Frame.HeaderSize + (cipher is null ? 0 : Frame.MacSize);
        int payloadSize = Compress(packet, destination[payloadStart..], _snappyLogger);
        destination[0] = (byte)(payloadSize >> 16);
        destination[1] = (byte)(payloadSize >> 8);
        destination[2] = (byte)payloadSize;
        destination[3] = 194;
        destination[4] = 128;
        destination[5] = 128;
        destination[6..Frame.HeaderSize].Clear();
        int padding = Frame.CalculatePadding(payloadSize);
        destination.Slice(payloadStart + payloadSize, padding).Clear();
        int length = payloadStart + payloadSize + padding;
        if (cipher is not null)
        {
            EncryptFrame(destination, payloadSize + padding);
            length += Frame.MacSize;
        }

        return length;
    }

    /// <summary>Writes <paramref name="packet"/> with its packet type as is and its content compressed with Snappy.</summary>
    /// <param name="destination">At least <see cref="Snappier.Snappy.GetMaxCompressedLength"/> of the packet length.</param>
    /// <returns>The number of bytes written.</returns>
    internal static int Compress(ReadOnlySpan<byte> packet, Span<byte> destination, ILogger logger)
    {
        int packetTypeLength = new RlpReader(packet).ReadEncodedItem().Length;
        packet[..packetTypeLength].CopyTo(destination);
        ReadOnlySpan<byte> content = packet[packetTypeLength..];

        if (logger.IsTrace) TraceCompressing(content.Length);

        return packetTypeLength + Snappier.Snappy.Compress(content, destination[packetTypeLength..]);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCompressing(int contentLength) => logger.Trace($"Compressing with Snappy a message of length {contentLength}");
    }

    /// <summary>Encrypts a frame in place and writes its header and frame MACs into their slots.</summary>
    private void EncryptFrame(Span<byte> frame, int payloadSize)
    {
        Span<byte> header = frame[..Frame.HeaderSize];
        cipher!.Encrypt(header, header);
        macProcessor!.AddMac(header, frame.Slice(Frame.HeaderSize, Frame.MacSize), true);
        Span<byte> payload = frame.Slice(Frame.HeaderSize + Frame.MacSize, payloadSize);
        cipher.Encrypt(payload, payload);
        macProcessor.AddMac(payload, frame.Slice(Frame.HeaderSize + Frame.MacSize + payloadSize, Frame.MacSize), false);
    }
}
