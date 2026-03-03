// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Network.Rlpx;
using NSubstitute;

namespace Nethermind.Network.Test.Rlpx.TestWrappers
{
    internal class ZeroFrameDecoderTestWrapper : ZeroFrameDecoder
    {
        private readonly IChannelHandlerContext _context;

        public ZeroFrameDecoderTestWrapper(IFrameCipher frameCipher, FrameMacProcessor frameMacProcessor) : base(frameCipher, frameMacProcessor)
        {
            _context = Substitute.For<IChannelHandlerContext>();
            _context.Allocator.Returns(PooledByteBufferAllocator.Default);
        }

        public IByteBuffer Decode(IByteBuffer input, bool throwOnCorruptedFrames = true)
        {
            List<object> result = new();
            try
            {
                base.Decode(_context, input, result);
            }
            catch (CorruptedFrameException)
            {
                if (throwOnCorruptedFrames)
                {
                    throw;
                }
            }

            if (result.Count != 0)
            {
                return (IByteBuffer)result[0];
            }

            return null;
        }
    }

    /// <summary>
    /// A cipher that ignores input and always returns predetermined bytes on decrypt.
    /// Used to control what ReadFrameSize sees after MAC authentication passes.
    /// </summary>
    internal class OverrideDecryptCipher(byte[] decryptOutput) : IFrameCipher
    {
        public void Encrypt(byte[] input, int offset, int length, byte[] output, int outputOffset) =>
            Array.Copy(input, offset, output, outputOffset, length);

        public void Decrypt(byte[] input, int offset, int length, byte[] output, int outputOffset) =>
            Array.Copy(decryptOutput, 0, output, outputOffset, length);
    }
}
