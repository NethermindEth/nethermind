// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx;

public class ZeroNettyFrameEncodeDecodeTests
{
    private const int TestLength = 10000;

    [Test]
    public void Enabling_snappy_during_encoding_preserves_current_frames()
    {
        (EncryptionSecrets expectedSecrets, _) = NetTestVectors.GetSecretsPair();
        (EncryptionSecrets actualSecrets, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor expectedMac = new(expectedSecrets);
        using FrameMacProcessor actualMac = new(actualSecrets);
        ZeroPacketSplitter? splitter = null;
        CallbackCipher switchingCipher = new(new FrameCipher(actualSecrets.AesSecret), () => splitter!.EnableSnappy(LimboLogs.Instance));
        splitter = new(switchingCipher, actualMac);
        EmbeddedChannel expectedChannel = new(new ZeroPacketSplitter(new FrameCipher(expectedSecrets.AesSecret), expectedMac));
        EmbeddedChannel actualChannel = new(splitter);
        try
        {
            int length = Frame.DefaultMaxFrameSize * 2 + 1;
            expectedChannel.WriteOutbound(Unpooled.Buffer(length).WriteZero(length));
            actualChannel.WriteOutbound(Unpooled.Buffer(length).WriteZero(length));
            using DisposableByteBuffer expected = expectedChannel.ReadOutbound<IByteBuffer>().AsDisposable();
            using DisposableByteBuffer actual = actualChannel.ReadOutbound<IByteBuffer>().AsDisposable();
            Assert.That(actual.AsSpan(), Is.SequenceEqualTo(expected.AsSpan()));
        }
        finally
        {
            expectedChannel.FinishAndReleaseAll();
            actualChannel.FinishAndReleaseAll();
        }
    }

    [Test]
    public void Enabling_snappy_after_sizing_the_output_encodes_the_packet_with_snappy()
    {
        (EncryptionSecrets expectedSecrets, _) = NetTestVectors.GetSecretsPair();
        (EncryptionSecrets actualSecrets, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor expectedMac = new(expectedSecrets);
        using FrameMacProcessor actualMac = new(actualSecrets);
        ZeroPacketSplitter expectedSplitter = new(new FrameCipher(expectedSecrets.AesSecret), expectedMac);
        expectedSplitter.EnableSnappy(LimboLogs.Instance);
        EmbeddedChannel expectedChannel = new(expectedSplitter);
        CombinedEncoder actual = new(new FrameCipher(actualSecrets.AesSecret), actualMac);
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(PooledByteBufferAllocator.Default);
        // Random content does not compress, so Snappy needs more room than the framed size the output gets.
        byte[] packet = new byte[6 * 1024];
        new Random(7).NextBytes(packet);
        packet[0] = 2;
        try
        {
            using DisposableByteBuffer input = Unpooled.WrappedBuffer(packet).AsDisposable();
            Assert.That(actual.AcceptOutboundMessage(input), Is.True);
            using DisposableByteBuffer output = actual.Allocate(context).AsDisposable();
            actual.EnableSnappy(LimboLogs.Instance);
            actual.Encode(input, output);

            expectedChannel.WriteOutbound(Unpooled.WrappedBuffer(packet));
            using DisposableByteBuffer expected = expectedChannel.ReadOutbound<IByteBuffer>().AsDisposable();
            Assert.That(output.AsSpan(), Is.SequenceEqualTo(expected.AsSpan()));
        }
        finally
        {
            expectedChannel.FinishAndReleaseAll();
        }
    }

    [Test]
    public void Packet_over_buffer_components_encodes_as_in_one_buffer([Values] bool snappy)
    {
        (EncryptionSecrets contiguousSecrets, _) = NetTestVectors.GetSecretsPair();
        (EncryptionSecrets segmentedSecrets, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor contiguousMac = new(contiguousSecrets);
        using FrameMacProcessor segmentedMac = new(segmentedSecrets);
        ZeroPacketSplitter contiguousSplitter = new(new FrameCipher(contiguousSecrets.AesSecret), contiguousMac);
        ZeroPacketSplitter segmentedSplitter = new(new FrameCipher(segmentedSecrets.AesSecret), segmentedMac);
        if (snappy)
        {
            contiguousSplitter.EnableSnappy(LimboLogs.Instance);
            segmentedSplitter.EnableSnappy(LimboLogs.Instance);
        }

        EmbeddedChannel contiguous = new(contiguousSplitter);
        EmbeddedChannel segmented = new(segmentedSplitter);
        byte[] packet = new byte[Frame.DefaultMaxFrameSize * 2 + 100];
        new Random(11).NextBytes(packet);
        packet[0] = 2;
        try
        {
            contiguous.WriteOutbound(Unpooled.WrappedBuffer(packet));
            segmented.WriteOutbound(Unpooled.WrappedBuffer(Unpooled.WrappedBuffer(packet, 0, 700), Unpooled.WrappedBuffer(packet, 700, packet.Length - 700)));
            using DisposableByteBuffer expected = contiguous.ReadOutbound<IByteBuffer>().AsDisposable();
            using DisposableByteBuffer actual = segmented.ReadOutbound<IByteBuffer>().AsDisposable();
            Assert.That(actual.AsSpan(), Is.SequenceEqualTo(expected.AsSpan()));
        }
        finally
        {
            contiguous.FinishAndReleaseAll();
            segmented.FinishAndReleaseAll();
        }
    }

    [Test]
    public async Task Combined_encoder_releases_buffers_when_encryption_fails([Values(1, 2)] int failingCall, [Values] bool snappy)
    {
        using PooledBufferLeakDetector detector = new();
        int calls = 0;
        CallbackCipher cipher = new(new FrameCipher(NetTestVectors.AesSecret), () =>
        {
            if (++calls == failingCall) throw new InvalidOperationException("Encryption failed");
        });
        (EncryptionSecrets secrets, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor mac = new(secrets);
        ZeroPacketSplitter splitter = new(cipher, mac);
        if (snappy) splitter.EnableSnappy(LimboLogs.Instance);
        IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
        context.Allocator.Returns(detector.Allocator);
        IByteBuffer input = detector.Allocator.Buffer(17).WriteZero(17);

        await Assert.ThrowsAsync<EncoderException>(async () => await splitter.WriteAsync(context, input));
        Assert.That(input.ReferenceCount, Is.Zero);
        await context.DidNotReceive().WriteAsync(Arg.Any<object>());
    }

    [Test]
    public void Combined_encoder_matches_separate_stages(
        [Values(1, 15, 16, 17, 1023, 1024, 1025, 2048, 4097, 65535, 65536, 65537, 1048575, 1048576, 1048577)] int length,
        [Values] bool disableFraming)
    {
        (EncryptionSecrets oldSecrets, _) = NetTestVectors.GetSecretsPair();
        (EncryptionSecrets newSecrets, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor oldMac = new(oldSecrets);
        using FrameMacProcessor newMac = new(newSecrets);
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
            newSplitter.EnableSnappy(LimboLogs.Instance);
        }
        byte[] payload = new byte[length];
        new Random(42).NextBytes(payload);
        payload[0] = 2;
        try
        {
            // Cross the RLP context-id encoding boundary and exercise continuous cipher/MAC state.
            for (int i = 0; i < (length <= 65536 ? 130 : 2); i++)
            {
                IByteBuffer oldInput = Unpooled.Buffer(length + 7).WriteZero(7).WriteBytes(payload).SkipBytes(7);
                IByteBuffer newInput = Unpooled.Buffer(length + 7).WriteZero(7).WriteBytes(payload).SkipBytes(7);
                oldChannel.WriteOutbound(oldInput);
                newChannel.WriteOutbound(newInput);
                using DisposableByteBuffer expected = oldChannel.ReadOutbound<IByteBuffer>().AsDisposable();
                using DisposableByteBuffer actual = newChannel.ReadOutbound<IByteBuffer>().AsDisposable();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(actual.AsSpan(), Is.SequenceEqualTo(expected.AsSpan()), $"message {i}");
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
    public void Combined_encoder_initializes_wire_bytes_and_preserves_sentinels(
        [Values(15, 16, 17, 1025, 65537)] int length, [Values] bool snappy)
    {
        (EncryptionSecrets a, _) = NetTestVectors.GetSecretsPair();
        (EncryptionSecrets b, _) = NetTestVectors.GetSecretsPair();
        using FrameMacProcessor referenceMac = new(a);
        using FrameMacProcessor combinedMac = new(b);
        ZeroPacketSplitter referenceSplitter = new();
        CombinedEncoder combined = new(new FrameCipher(b.AesSecret), combinedMac);
        EmbeddedChannel reference = new(new ZeroFrameEncoder(new FrameCipher(a.AesSecret), referenceMac), referenceSplitter);
        if (snappy)
        {
            referenceSplitter.DisableFraming();
            reference.Pipeline.AddLast(new ZeroSnappyEncoder(LimboLogs.Instance));
            combined.EnableSnappy(LimboLogs.Instance);
        }
        byte[] payload = new byte[length];
        new Random(13592).NextBytes(payload);
        payload[0] = 2;
        try
        {
            reference.WriteOutbound(Unpooled.WrappedBuffer(payload));
            using DisposableByteBuffer expected = reference.ReadOutbound<IByteBuffer>().AsDisposable();
            byte[] dirty = new byte[expected.ReadableBytes * 2 + 512];
            dirty.AsSpan().Fill(0xa5);
            using DisposableByteBuffer backing = Unpooled.WrappedBuffer(dirty).AsDisposable();
            IByteBuffer output = backing.Slice(13, dirty.Length - 32).SetIndex(7, 7);
            using DisposableByteBuffer input = Unpooled.WrappedBuffer(payload).AsDisposable();
            combined.Encode(input, output);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(output.AsSpan(), Is.SequenceEqualTo(expected.AsSpan()));
                Assert.That(dirty.AsSpan(0, 20).ToArray(), Is.All.EqualTo(0xa5));
                Assert.That(dirty.AsSpan(dirty.Length - 19).ToArray(), Is.All.EqualTo(0xa5));
            }
        }
        finally
        {
            reference.FinishAndReleaseAll();
        }
    }

    /// <summary>Encrypts with <paramref name="cipher"/>, then runs <paramref name="afterEncrypt"/>.</summary>
    /// <remarks>Stands in for a substitute, which cannot intercept calls with span arguments.</remarks>
    private sealed class CallbackCipher(IFrameCipher cipher, Action afterEncrypt) : IFrameCipher
    {
        public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output)
        {
            cipher.Encrypt(input, output);
            afterEncrypt();
        }

        public void Decrypt(ReadOnlySpan<byte> input, Span<byte> output) => cipher.Decrypt(input, output);
    }

    private sealed class CombinedEncoder(IFrameCipher cipher, IFrameMacProcessor mac)
        : ZeroPacketSplitter(cipher, mac)
    {
        public void Encode(IByteBuffer input, IByteBuffer output) => base.Encode(null, input, output);

        public IByteBuffer Allocate(IChannelHandlerContext context) => AllocateBuffer(context);
    }

    [Test]
    public async Task TwoWayConcurrentEncodeDecodeTests()
    {
        (EncryptionSecrets A, EncryptionSecrets B) = NetTestVectors.GetSecretsPair();

        FrameCipher frameCipher = new(B.AesSecret);
        FrameMacProcessor macProcessor = new(B);

        FrameCipher frameCipher2 = new(A.AesSecret);
        FrameMacProcessor macProcessor2 = new(A);

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
                NettyBufferPacket packet = (NettyBufferPacket)info[0];
                RlpReader ctx = new(packet.Buffer.AsSpan());
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

            byte[] encByte = Rlp.Encode(input);
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
