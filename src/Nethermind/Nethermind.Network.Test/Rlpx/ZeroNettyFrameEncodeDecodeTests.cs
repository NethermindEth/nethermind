// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
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
    public void Combined_encoder_releases_buffers_when_encryption_fails([Values(1, 2)] int failingCall)
    {
        using PooledBufferLeakDetector detector = new();
        IFrameCipher cipher = Substitute.For<IFrameCipher>();
        int calls = 0;
        cipher.When(x => x.Encrypt(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<int>()))
            .Do(_ =>
            {
                if (++calls == failingCall) throw new InvalidOperationException("Encryption failed");
            });
        ZeroPacketSplitter splitter = new(cipher, Substitute.For<IFrameMacProcessor>());
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        IByteBuffer input = detector.Allocator.Buffer(17).WriteZero(17);

        Assert.ThrowsAsync<EncoderException>(async () => await splitter.WriteAsync(context, input));
        Assert.That(input.ReferenceCount, Is.Zero);
        context.DidNotReceive().WriteAsync(Arg.Any<object>());
    }

    [Test]
    public void Combined_encoder_matches_separate_stages(
        [Values(1, 15, 16, 17, 1023, 1024, 1025, 2048, 4097)] int length,
        [Values] bool disableFraming)
    {
        (EncryptionSecrets oldSecrets, _) = NetTestVectors.GetSecretsPair();
        (EncryptionSecrets newSecrets, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor oldMac = new(TestItem.IgnoredPublicKey, oldSecrets);
        using FrameMacProcessor newMac = new(TestItem.IgnoredPublicKey, newSecrets);
        ZeroPacketSplitter oldSplitter = new();
        ZeroPacketSplitter newSplitter = new(new FrameCipher(newSecrets.AesSecret), newMac);
        if (disableFraming)
        {
            oldSplitter.DisableFraming();
            newSplitter.DisableFraming();
        }

        EmbeddedChannel oldChannel = new(new ZeroFrameEncoder(new FrameCipher(oldSecrets.AesSecret), oldMac), oldSplitter);
        EmbeddedChannel newChannel = new(newSplitter);
        if (disableFraming)
        {
            oldChannel.Pipeline.AddLast(new ZeroSnappyEncoder(LimboLogs.Instance));
            newChannel.Pipeline.AddLast(new ZeroSnappyEncoder(LimboLogs.Instance));
        }
        byte[] payload = new byte[length];
        new Random(42).NextBytes(payload);
        payload[0] = 2;
        try
        {
            // Cross the RLP context-id encoding boundary and exercise continuous cipher/MAC state.
            for (int i = 0; i < 130; i++)
            {
                IByteBuffer oldInput = Unpooled.Buffer(length + 7).WriteZero(7).WriteBytes(payload).SkipBytes(7);
                IByteBuffer newInput = oldInput.Copy();
                oldChannel.WriteOutbound(oldInput);
                newChannel.WriteOutbound(newInput);
                using DisposableByteBuffer expected = oldChannel.ReadOutbound<IByteBuffer>().AsDisposable();
                using DisposableByteBuffer actual = newChannel.ReadOutbound<IByteBuffer>().AsDisposable();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual.AsSpan().ToArray(), Is.EqualTo(expected.AsSpan().ToArray()), $"message {i}");
                    Assert.That(oldInput.ReferenceCount, Is.Zero);
                    Assert.That(newInput.ReferenceCount, Is.Zero);
                }
            }
        }
        finally
        {
            oldChannel.FinishAndReleaseAll();
            newChannel.FinishAndReleaseAll();
        }
    }

    [Test]
    public async Task TwoWayConcurrentEncodeDecodeTests()
    {
        (EncryptionSecrets A, EncryptionSecrets B) = NetTestVectors.GetSecretsPair();

        FrameCipher frameCipher = new(B.AesSecret);
        FrameMacProcessor macProcessor = new(TestItem.IgnoredPublicKey, B);

        FrameCipher frameCipher2 = new(A.AesSecret);
        FrameMacProcessor macProcessor2 = new(TestItem.IgnoredPublicKey, A);

        Task t1 = Task.Factory.StartNew(() => RunStreamTests(frameCipher, macProcessor, frameCipher2, macProcessor2), TaskCreationOptions.LongRunning);
        Task t2 = Task.Factory.StartNew(() => RunStreamTests(frameCipher2, macProcessor2, frameCipher, macProcessor), TaskCreationOptions.LongRunning);

        await t1;
        await t2;
    }

    private async Task RunStreamTests(FrameCipher frameCipher, FrameMacProcessor macProcessor, FrameCipher frameCipher2, FrameMacProcessor macProcessor2)
    {
        ZeroPacketSplitter splitter = new();
        ZeroFrameEncoder encoder = new(frameCipher, macProcessor);

        ZeroFrameDecoder decoder = new(frameCipher2, macProcessor2);
        ZeroFrameMerger frameMerger = new(LimboLogs.Instance);

        IByteBuffer reDecoded = null;

        IChannelHandlerContext recordWrite = Substitute.For<IChannelHandlerContext>();
        recordWrite.When((it) => it.FireChannelRead(Arg.Any<object>()))
            .Do((info =>
            {
                ZeroPacket packet = (ZeroPacket)info[0];
                RlpReader ctx = new(packet.Content.AsSpan());
                byte[] bytes = ctx.DecodeByteArray();
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
            using DisposableByteBuffer buffer = Unpooled.Buffer(encByte.Length + 1).AsDisposable();
            buffer.WriteByte(0);
            buffer.WriteBytes(encByte);
            await splitter.WriteAsync(encoderWrite, buffer);

            Assert.That(reDecoded.Array, Is.EqualTo(input));
        }
    }

    private IChannelHandlerContext PipeWriteToChannel(IChannelHandler channelHandler, IChannelHandlerContext nextContext)
    {
        IChannelHandlerContext pipeWrite = Substitute.For<IChannelHandlerContext>();
        pipeWrite.When((it) => it.WriteAsync(Arg.Any<object>()))
            .Do((info =>
            {
                if (info[0] is IReferenceCounted refCount)
                {
                    refCount.Retain();
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
                if (info[0] is IReferenceCounted refCount)
                {
                    refCount.Retain();
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
