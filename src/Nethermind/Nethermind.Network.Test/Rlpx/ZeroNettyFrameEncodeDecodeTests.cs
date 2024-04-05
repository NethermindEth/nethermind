// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common;
using DotNetty.Transport.Channels;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx;

public class ZeroNettyFrameEncodeDecodeTests
{
    private const int TestLength = 10000;

    [Test]
    public async Task TwoWayConcurrentEncodeDecodeTests()
    {
        var secrets = NetTestVectors.GetSecretsPair();

        var frameCipher = new FrameCipher(secrets.B.AesSecret);
        var macProcessor = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.B);

        var frameCipher2 = new FrameCipher(secrets.A.AesSecret);
        var macProcessor2 = new FrameMacProcessor(TestItem.IgnoredPublicKey, secrets.A);

        Task t1 = Task.Factory.StartNew(() => RunStreamTests(frameCipher, macProcessor, frameCipher2, macProcessor2), TaskCreationOptions.LongRunning);
        Task t2 = Task.Factory.StartNew(() => RunStreamTests(frameCipher2, macProcessor2, frameCipher, macProcessor), TaskCreationOptions.LongRunning);

        await t1;
        await t2;
    }

    private async Task RunStreamTests(FrameCipher frameCipher, FrameMacProcessor macProcessor, FrameCipher frameCipher2, FrameMacProcessor macProcessor2)
    {
        ZeroPacketSplitter splitter = new(LimboLogs.Instance);
        ZeroFrameEncoder encoder = new(frameCipher, macProcessor, LimboLogs.Instance);

        ZeroFrameDecoder decoder = new(frameCipher2, macProcessor2, LimboLogs.Instance);
        ZeroFrameMerger frameMerger = new(LimboLogs.Instance);

        IByteBuffer reDecoded = null;

        IChannelHandlerContext recordWrite = Substitute.For<IChannelHandlerContext>();
        recordWrite.When((it) => it.FireChannelRead(Arg.Any<object>()))
            .Do((info =>
            {
                ZeroPacket packet = (ZeroPacket)info[0];
                NettyRlpStream rlpStream = new NettyRlpStream(packet.Content);
                byte[] bytes = rlpStream.DecodeByteArray();
                reDecoded.WriteBytes(bytes);
            }));

        IChannelHandlerContext mergerWrite = PipeReadToChannel(frameMerger, recordWrite);
        IChannelHandlerContext decoderWrite = PipeWriteToChannelRead(decoder, mergerWrite);
        IChannelHandlerContext encoderWrite = PipeWriteToChannel(encoder, decoderWrite);

        for (int i = 0; i < TestLength; i++)
        {
            int size = 1 + Random.Shared.Next() % 1024;
            reDecoded = Unpooled.Buffer(size);
            byte[] input = new byte[size];
            Random.Shared.NextBytes(input);

            byte[] encByte = Rlp.Encode(input).Bytes;
            IByteBuffer buffer = Unpooled.Buffer(encByte.Length + 1);
            buffer.WriteByte(0);
            buffer.WriteBytes(encByte);
            await splitter.WriteAsync(encoderWrite, buffer);

            reDecoded.Array.Should().BeEquivalentTo(input);
        }
    }

    private IChannelHandlerContext PipeWriteToChannel(IChannelHandler channelHandler, IChannelHandlerContext nextContext)
    {
        IChannelHandlerContext pipeWrite = Substitute.For<IChannelHandlerContext>();
        pipeWrite.When((it) => it.WriteAsync(Arg.Any<object>()))
            .Do((info =>
            {
                if (info[0] is IReferenceCounted refc)
                {
                    refc.Retain();
                }
                channelHandler.WriteAsync(nextContext, info[0]).Wait();
            }));
        pipeWrite.Allocator.Returns(UnpooledByteBufferAllocator.Default);
        return pipeWrite;
    }

    private IChannelHandlerContext PipeWriteToChannelRead(IChannelHandler channelHandler, IChannelHandlerContext nextContext)
    {
        IChannelHandlerContext pipeWrite = Substitute.For<IChannelHandlerContext>();
        pipeWrite.When((it) => it.WriteAsync(Arg.Any<object>()))
            .Do((info =>
            {
                if (info[0] is IReferenceCounted refc)
                {
                    refc.Retain();
                }
                channelHandler.ChannelRead(nextContext, info[0]);
            }));
        pipeWrite.Allocator.Returns(UnpooledByteBufferAllocator.Default);
        return pipeWrite;
    }

    private IChannelHandlerContext PipeReadToChannel(IChannelHandler channelHandler, IChannelHandlerContext nextContext)
    {
        IChannelHandlerContext pipeWrite = Substitute.For<IChannelHandlerContext>();
        pipeWrite.When((it) => it.FireChannelRead(Arg.Any<object>()))
            .Do((info =>
            {
                channelHandler.ChannelRead(nextContext, info[0]);
            }));
        pipeWrite.Allocator.Returns(UnpooledByteBufferAllocator.Default);
        return pipeWrite;
    }

}
