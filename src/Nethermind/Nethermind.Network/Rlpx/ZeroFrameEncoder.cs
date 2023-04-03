// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Logging;

namespace Nethermind.Network.Rlpx
{
    public class ZeroFrameEncoder : MessageToByteEncoder<IByteBuffer>
    {
        private readonly ILogger _logger;
        private readonly IFrameCipher _frameCipher;
        private readonly IFrameMacProcessor _frameMacProcessor;
        private readonly FrameHeaderReader _headerReader = new();

        private readonly byte[] _encryptBuffer = new byte[Frame.BlockSize];
        private readonly byte[] _macBuffer = new byte[16];

        public ZeroFrameEncoder(IFrameCipher frameCipher, IFrameMacProcessor frameMacProcessor, ILogManager logManager)
        {
            _frameCipher = frameCipher ?? throw new ArgumentNullException(nameof(frameCipher));
            _frameMacProcessor = frameMacProcessor ?? throw new ArgumentNullException(nameof(frameMacProcessor));
            _logger = logManager?.GetClassLogger<ZeroFrameEncoder>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        protected override void Encode(IChannelHandlerContext context, IByteBuffer input, IByteBuffer output)
        {
            while (input.IsReadable())
            {
                if (input.ReadableBytes % Frame.BlockSize != 0)
                {
                    throw new CorruptedFrameException($"Frame prepared for sending was in incorrect format: length was {input.ReadableBytes}");
                }

                FrameHeaderReader.FrameInfo frame = _headerReader.ReadFrameHeader(input);

                // 0 if the buffer has enough writable bytes, and its capacity is unchanged.
                // 1 if the buffer does not have enough bytes, and its capacity is unchanged.
                // 2 if the buffer has enough writable bytes, and its capacity has been increased.
                // 3 if the buffer does not have enough bytes, but its capacity has been increased to its maximum.
                int code = output.EnsureWritable(Frame.HeaderSize + Frame.MacSize + frame.PayloadSize + Frame.MacSize, true);

                WriteHeader(output);
                WriteHeaderMac(output);
                for (int i = 0; i < frame.PayloadSize / Frame.BlockSize; i++)
                {
                    WritePayloadBlock(input, output);
                }

                WritePayloadMac(output);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteHeader(IByteBuffer output)
        {
            _frameCipher.Encrypt(_headerReader.HeaderBytes, 0, 16, _encryptBuffer, 0);
            output.WriteBytes(_encryptBuffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteHeaderMac(IByteBuffer output)
        {
            _frameMacProcessor.AddMac(_encryptBuffer, 0, 16, _macBuffer, 0, true);
            output.WriteBytes(_macBuffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WritePayloadMac(IByteBuffer output)
        {
            _frameMacProcessor.CalculateMac(_macBuffer);
            output.WriteBytes(_macBuffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WritePayloadBlock(IByteBuffer input, IByteBuffer output)
        {
            input.ReadBytes(_encryptBuffer);
            _frameCipher.Encrypt(_encryptBuffer, 0, 16, _encryptBuffer, 0);
            _frameMacProcessor.UpdateEgressMac(_encryptBuffer);
            output.WriteBytes(_encryptBuffer);
        }
    }
}
