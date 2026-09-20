// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Serialization.Rlp;
using Nethermind.Logging;
using System;
using System.Threading;

namespace Nethermind.Network.Rlpx
{
    public class ZeroPacketSplitter() : MessageToByteEncoder<IByteBuffer>
    {
        private const int Framed = 0;
        private const int Unframed = 1;
        private const int Snappy = 2;

        /// <remarks>Unframed encoding is retained for wire-equivalence tests and outbound benchmarks.</remarks>
        internal void DisableFraming() => Interlocked.CompareExchange(ref _encodingMode, Unframed, Framed);

        private int _contextId;
        private ILogger _snappyLogger;
        private int _encodingMode;

        internal void EnableSnappy(ILogManager logManager)
        {
            _snappyLogger = logManager.GetClassLogger<ZeroSnappyEncoder>();
            Volatile.Write(ref _encodingMode, Snappy);
        }

        private readonly IFrameCipher? _cipher;
        private readonly IFrameMacProcessor? _macProcessor;
        private readonly byte[]? _headerBuffer;

        internal ZeroPacketSplitter(IFrameCipher cipher, IFrameMacProcessor macProcessor) : this()
        {
            _cipher = cipher;
            _macProcessor = macProcessor;
            _headerBuffer = new byte[Frame.HeaderSize];
        }

        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            int encodingMode = Volatile.Read(ref _encodingMode);
            if (encodingMode == Snappy)
            {
                EncodeCompressed(input, output, _snappyLogger);
                return;
            }

            Interlocked.Increment(ref _contextId);

            int totalPayloadSize = input.ReadableBytes;
            int maxFrameSize = encodingMode == Framed ? Frame.DefaultMaxFrameSize : int.MaxValue;

            int framesCount = (totalPayloadSize - 1) / maxFrameSize + 1;
            for (int i = 0; i < framesCount; i++)
            {
                int totalPayloadOffset = maxFrameSize * i;
                int framePayloadSize = Math.Min(maxFrameSize, totalPayloadSize - totalPayloadOffset);
                int paddingSize = i == framesCount - 1 ? Frame.CalculatePadding(totalPayloadSize) : 0;
                int frameStart = output.WriterIndex;
                output.EnsureWritable(Frame.HeaderSize + framePayloadSize + paddingSize + (_cipher is null ? 0 : 2 * Frame.MacSize));

                // 000 - 016 | header
                // 016 - 01x | packet type
                // 01x - frm | payload
                // frm - %16 | padding to 16

                // here we encode payload size as an RLP encoded long value without leading zeros
                /*0*/
                output.WriteByte((byte)(framePayloadSize >> 16));
                /*1*/
                output.WriteByte((byte)(framePayloadSize >> 8));
                /*2*/
                output.WriteByte((byte)framePayloadSize);

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
                    /*3*/
                    output.WriteByte(194);
                    /*4*/
                    output.WriteByte(128);
                    /*5*/
                    output.WriteByte(128);
                    /*6-16*/
                    output.WriteZero(10);
                }
                else
                {
                    int contentLength = Rlp.LengthOf(_contextId) + Rlp.LengthOf(0);
                    if (i == 0)
                    {
                        contentLength += Rlp.LengthOf(totalPayloadSize);
                    }
                    output.EnsureWritable(Rlp.LengthOfSequence(contentLength));
                    ByteBufferRlpWriter writer = new(output);
                    writer.StartSequence(contentLength);
                    writer.Encode(0);
                    writer.Encode(_contextId);
                    if (i == 0)
                    {
                        writer.Encode(totalPayloadSize);
                    }
                    output.WriteZero(Frame.HeaderSize - Rlp.LengthOfSequence(contentLength) - 3);
                }

                if (_cipher is not null) output.WriteZero(Frame.MacSize);

                /*message*/
                input.ReadBytes(output, framePayloadSize);
                /*padding to 16*/
                output.WriteZero(paddingSize);

                if (_cipher is not null)
                {
                    output.WriteZero(Frame.MacSize);
                    EncryptFrame(output, frameStart, framePayloadSize + paddingSize);
                }
            }
        }

        private void EncodeCompressed(IByteBuffer input, IByteBuffer output, ILogger logger)
        {
            int frameStart = output.WriterIndex;
            output.WriteZero(Frame.HeaderSize + (_cipher is null ? 0 : Frame.MacSize));
            int payloadStart = output.WriterIndex;
            ZeroSnappyEncoder.Compress(input, output, logger);
            int payloadSize = output.WriterIndex - payloadStart;
            output.SetByte(frameStart, payloadSize >> 16);
            output.SetByte(frameStart + 1, payloadSize >> 8);
            output.SetByte(frameStart + 2, payloadSize);
            output.SetByte(frameStart + 3, 194);
            output.SetByte(frameStart + 4, 128);
            output.SetByte(frameStart + 5, 128);
            int padding = Frame.CalculatePadding(payloadSize);
            output.WriteZero(padding);
            if (_cipher is not null)
            {
                output.WriteZero(Frame.MacSize);
                EncryptFrame(output, frameStart, payloadSize + padding);
            }
        }

        private void EncryptFrame(IByteBuffer output, int frameStart, int payloadSize)
        {
            // Reserve MAC slots while framing so encryption can use the final wire buffer in place.
            output.GetBytes(frameStart, _headerBuffer);
            _cipher!.Encrypt(_headerBuffer!, 0, Frame.HeaderSize, _headerBuffer!, 0);
            output.SetBytes(frameStart, _headerBuffer);

            byte[] bytes = output.Array;
            int headerOffset = output.ArrayOffset + frameStart;
            _macProcessor!.AddMac(_headerBuffer!, 0, Frame.HeaderSize, bytes, headerOffset + Frame.HeaderSize, true);
            int payloadOffset = headerOffset + Frame.HeaderSize + Frame.MacSize;
            _cipher.Encrypt(bytes, payloadOffset, payloadSize, bytes, payloadOffset);
            _macProcessor.AddMac(bytes, payloadOffset, payloadSize, bytes, payloadOffset + payloadSize, false);
        }
    }
}
