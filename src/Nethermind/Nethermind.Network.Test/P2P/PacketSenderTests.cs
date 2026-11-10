// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PacketSenderTests
    {
        private static (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message) SetupChannel(
            bool isActive, int contentLength = 1, int lengthError = 0)
        {
            IMessageSerializationService serializer = new MessageSerializationService(
                SerializerInfo.Create(new TestSerializer { Length = contentLength, LengthError = lengthError }));

            TestMessage testMessage = new();

            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.IsWritable.Returns(true);
            channel.Active.Returns(isActive);
            context.Channel.Returns(channel);
            context.Allocator.Returns(UnpooledByteBufferAllocator.Default);

            return (context, serializer, testMessage);
        }

        /// <summary>Makes the context allocate from <paramref name="allocator"/> and returns the last buffer it allocated.</summary>
        private static Func<IByteBuffer> RecordAllocations(IChannelHandlerContext context, IByteBufferAllocator allocator)
        {
            IByteBuffer? allocated = null;
            IByteBufferAllocator recording = Substitute.For<IByteBufferAllocator>();
            recording.Buffer(Arg.Any<int>()).Returns(c => allocated = allocator.Buffer(c.Arg<int>()));
            context.Allocator.Returns(recording);
            return () => allocated!;
        }

        [TestCase(true, 1, Description = "Does send on active channel")]
        [TestCase(false, 0, Description = "Does not try to send on inactive channel")]
        public void Send_respects_channel_active_state(bool isActive, int expectedSendCount)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(testMessage);

            context.Received(expectedSendCount).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }

        [TestCaseSource(nameof(P2PMessages))]
        public void Sends_the_packet_type_then_the_message(Func<PacketSender, int> enqueue, string expected)
        {
            using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
            (IChannelHandlerContext context, _, _) = SetupChannel(isActive: true);
            IByteBuffer? sent = null;
            context.WriteAndFlushAsync(Arg.Do<object>(message => sent = (IByteBuffer)message)).Returns(Task.CompletedTask);
            PacketSender packetSender = new(container.Resolve<IMessageSerializationService>(), LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);

            int length = enqueue(packetSender);

            using DisposableByteBuffer packet = sent!.AsDisposable();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(length, Is.EqualTo(expected.Length / 2));
                Assert.That(packet.ReadAllHex(), Is.EqualTo(expected));
            }
        }

        private static IEnumerable<TestCaseData> P2PMessages()
        {
            yield return Case("Hello", new HelloMessage
            {
                P2PVersion = 5,
                ClientId = "Nethermind",
                ListenPort = 30303,
                NodeId = TestItem.PublicKeyA,
                Capabilities = new ArrayPoolList<Capability>(2) { new("ETH", 68), new("snap", 1) }
            }, "80f85f058a4e65746865726d696e64cdc58365746844c684736e61700182765fb840a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92");
            yield return Case("AddCapability", new AddCapabilityMessage(new Capability("Snap", 1)), "04c684736e617001");
            yield return Case("Disconnect", new DisconnectMessage(EthDisconnectReason.TooManyPeers), "01c104");
            yield return Case("Ping", PingMessage.Instance, "02c0");
            yield return Case("Pong", PongMessage.Instance, "03c0");

            static TestCaseData Case<T>(string name, T message, string expected) where T : P2PMessage
            {
                message.AdaptivePacketType = message.PacketType;
                Func<PacketSender, int> enqueue = packetSender => packetSender.Enqueue(message);
                return new TestCaseData(enqueue, expected).SetArgDisplayNames(name);
            }
        }

        [Test]
        public void Releases_the_buffer_when_serialization_fails([Values(-1, 1)] int lengthError)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) =
                SetupChannel(isActive: true, lengthError: lengthError);
            Func<IByteBuffer> allocated = RecordAllocations(context, UnpooledByteBufferAllocator.Default);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);

            Assert.That(() => packetSender.Enqueue(testMessage), Throws.InvalidOperationException.With.Message.Contains("wrote"));
            Assert.That(allocated().ReferenceCount, Is.Zero);
            context.DidNotReceive().WriteAndFlushAsync(Arg.Any<object>());
        }

        [Test]
        public async Task Send_after_delay_if_specified()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);

            TimeSpan delay = TimeSpan.FromMilliseconds(100);

            PacketSender packetSender = new(serializer, LimboLogs.Instance, delay);
            packetSender.HandlerAdded(context);
            packetSender.Enqueue(testMessage);

            await context.Received(0).WriteAndFlushAsync(Arg.Any<IByteBuffer>());

            await Task.Delay(delay * 3);

            await context.Received(1).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
        }

        [Test]
        public void Returns_buffer_when_delay_cannot_complete([Values(-2, 60000)] int delayMilliseconds)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) =
                SetupChannel(isActive: true, contentLength: 131071);
            Func<IByteBuffer> allocated = RecordAllocations(context, PooledByteBufferAllocator.Default);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.FromMilliseconds(delayMilliseconds));
            packetSender.HandlerAdded(context);
            IByteBuffer? buffer = null;
            try
            {
                packetSender.Enqueue(testMessage);
                buffer = allocated();
                packetSender.HandlerRemoved(context);

                Assert.That(() => buffer.ReferenceCount, Is.Zero.After(1000, 10));
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                if (buffer?.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        [Test]
        public void Does_not_release_buffer_after_pipeline_handoff([Values] bool writeFails)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);
            Func<IByteBuffer> allocated = RecordAllocations(context, PooledByteBufferAllocator.Default);
            context.WriteAndFlushAsync(Arg.Any<object>()).Returns(writeFails ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            IByteBuffer? buffer = null;
            try
            {
                packetSender.Enqueue(testMessage);
                buffer = allocated();
                packetSender.HandlerRemoved(context);

                context.Received(1).WriteAndFlushAsync(buffer);
                Assert.That(buffer.ReferenceCount, Is.EqualTo(1));
            }
            finally
            {
                if (buffer?.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        private class TestMessage : P2PMessage
        {
            public override int PacketType { get; } = 0;
            public override string Protocol { get; } = "";
        }

        private sealed class TestSerializer : IMessageSerializer<TestMessage>
        {
            public int Length { get; init; }

            public int LengthError { get; init; }

            public int GetLength(TestMessage message) => Length;

            public int Serialize(Span<byte> destination, TestMessage message) => Length + LengthError;

            public TestMessage Deserialize(ReadOnlySpan<byte> data) => throw new NotSupportedException();
        }
    }
}
