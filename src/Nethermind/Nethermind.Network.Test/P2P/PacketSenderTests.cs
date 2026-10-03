// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using DotNetty.Transport.Channels;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class PacketSenderTests
    {
        private (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message) SetupChannel(bool isActive)
        {
            IByteBuffer serialized = UnpooledByteBufferAllocator.Default.Buffer(2);
            IMessageSerializationService serializer = Substitute.For<IMessageSerializationService>();

            TestMessage testMessage = new();
            serializer.ZeroSerialize(testMessage).Returns(serialized);
            serialized.SafeRelease();

            IChannelHandlerContext context = Substitute.For<IChannelHandlerContext>();
            IChannel channel = Substitute.For<IChannel>();
            channel.IsWritable.Returns(true);
            channel.Active.Returns(isActive);
            context.Channel.Returns(channel);

            return (context, serializer, testMessage);
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
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(131072);
            buffer.WriteZero(131072);
            serializer.ZeroSerialize(testMessage, Arg.Any<IByteBufferAllocator>()).Returns(buffer);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.FromMilliseconds(delayMilliseconds));
            packetSender.HandlerAdded(context);
            try
            {
                packetSender.Enqueue(testMessage);
                packetSender.HandlerRemoved(context);

                Assert.That(() => buffer.ReferenceCount, Is.Zero.After(1000, 10));
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        [Test]
        public void Does_not_release_buffer_after_pipeline_handoff([Values] bool writeFails)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage testMessage) = SetupChannel(isActive: true);
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(2);
            serializer.ZeroSerialize(testMessage, Arg.Any<IByteBufferAllocator>()).Returns(buffer);
            context.WriteAndFlushAsync(buffer).Returns(writeFails ? Task.FromException(new InvalidOperationException()) : Task.CompletedTask);
            PacketSender packetSender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            packetSender.HandlerAdded(context);
            try
            {
                packetSender.Enqueue(testMessage);
                packetSender.HandlerRemoved(context);

                context.Received(1).WriteAndFlushAsync(buffer);
                Assert.That(buffer.ReferenceCount, Is.EqualTo(1));
            }
            finally
            {
                if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }
        [Test]
        public void Default_backpressure_drops_without_serializing_or_retaining([Values("eth", "snap", "lean")] string protocol)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            context.Channel.IsWritable.Returns(false);
            TestMessage message = new(protocol);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            try
            {
                Assert.That(sender.Enqueue(message), Is.Zero);
                context.Channel.IsWritable.Returns(true);
                sender.ChannelWritabilityChanged(context);
                serializer.DidNotReceive().ZeroSerialize(message, Arg.Any<IByteBufferAllocator>());
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally { sender.HandlerRemoved(context); }
        }

        [Test]
        public async Task Default_serialization_does_not_block_channel_events()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message) = SetupChannel(true);
            using ManualResetEventSlim started = new();
            using ManualResetEventSlim release = new();
            using DisposableByteBuffer buffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(message, Arg.Any<IByteBufferAllocator>()).Returns(_ =>
            {
                started.Set();
                Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                return buffer;
            });
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            Task<int> send = Task.Run(() => sender.Enqueue(message));
            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                await Task.Run(() => sender.ChannelWritabilityChanged(context)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(send.IsCompleted, Is.False);
            }
            finally
            {
                release.Set();
                await send.WaitAsync(TimeSpan.FromSeconds(5));
                sender.HandlerRemoved(context);
            }
        }

        [Test]
        public async Task Bulk_session_serialization_does_not_block_channel_events([Values] bool asyncChunk)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage message = new(asyncChunk ? "lean" : "eth", asyncChunk ? 1 : 0);
            using ManualResetEventSlim started = new();
            using ManualResetEventSlim release = new();
            using DisposableByteBuffer buffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(message, Arg.Any<IByteBufferAllocator>()).Returns(_ =>
            {
                started.Set();
                Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                return buffer;
            });
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            Task<int> send = Task.Run(async () => asyncChunk
                ? await sender.EnqueueAsync(message, CancellationToken.None)
                : sender.Enqueue(message));
            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                await Task.Run(() => sender.ChannelWritabilityChanged(context)).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(send.IsCompleted, Is.False);
            }
            finally
            {
                release.Set();
                await send.WaitAsync(TimeSpan.FromSeconds(5));
                sender.HandlerRemoved(context);
            }
        }

        [Test]
        public async Task Inline_control_can_serialize_while_a_bulk_codec_is_busy()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage chunk = new("lean", 1), control = new("p2p");
            using ManualResetEventSlim started = new(), release = new();
            using DisposableByteBuffer chunkBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            using DisposableByteBuffer controlBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>()).Returns(_ =>
            {
                started.Set();
                Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                return chunkBuffer;
            });
            serializer.ZeroSerialize(control, Arg.Any<IByteBufferAllocator>()).Returns(controlBuffer);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            Task<int> bulk = Task.Run(async () => await sender.EnqueueAsync(chunk, CancellationToken.None));
            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(sender.Enqueue(control), Is.EqualTo(4));
                _ = context.Received(1).WriteAndFlushAsync(controlBuffer);
                release.Set();
                await bulk.WaitAsync(TimeSpan.FromSeconds(5));
                Received.InOrder(() => { context.WriteAndFlushAsync(controlBuffer); context.WriteAndFlushAsync(chunkBuffer); });
            }
            finally { release.Set(); await bulk.WaitAsync(TimeSpan.FromSeconds(5)); sender.HandlerRemoved(context); }
        }

        [Test]
        public async Task Writable_channel_preserves_sixty_four_concurrent_controls_without_silent_drops()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage first = new("eth");
            using ManualResetEventSlim started = new(), release = new();
            ConcurrentBag<IByteBuffer> buffers = [];
            serializer.ZeroSerialize(Arg.Any<TestMessage>(), Arg.Any<IByteBufferAllocator>()).Returns(call =>
            {
                IByteBuffer buffer = Unpooled.Buffer(4).WriteZero(4);
                buffers.Add(buffer);
                if (ReferenceEquals(call.Arg<TestMessage>(), first))
                {
                    started.Set();
                    Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                }
                return buffer;
            });
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            Task<int> firstSend = Task.Run(() => sender.Enqueue(first));
            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Task<int>[] controls = new Task<int>[63];
                for (int index = 0; index < controls.Length; index++)
                    controls[index] = Task.Run(() => sender.Enqueue(new TestMessage("eth")));
                int[] lengths = await Task.WhenAll(controls).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(lengths, Is.All.EqualTo(4));
                _ = context.DidNotReceive().CloseAsync();
                _ = context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
                release.Set();
                Assert.That(await firstSend.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(4));
                _ = context.Received(64).WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                release.Set();
                await firstSend.WaitAsync(TimeSpan.FromSeconds(5));
                sender.HandlerRemoved(context);
                foreach (IByteBuffer buffer in buffers) if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        [Test]
        public void Control_queue_overflow_closes_explicitly_and_releases_queued_buffers()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message) = SetupChannel(true);
            context.Channel.IsWritable.Returns(false);
            IByteBuffer[] buffers = new IByteBuffer[64];
            int encoded = 0;
            serializer.ZeroSerialize(message, Arg.Any<IByteBufferAllocator>()).Returns(_ =>
            {
                IByteBuffer buffer = Unpooled.Buffer(4).WriteZero(4);
                buffers[encoded++] = buffer;
                return buffer;
            });
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            try
            {
                for (int index = 0; index < 64; index++) Assert.That(sender.Enqueue(message), Is.EqualTo(4));
                Assert.That(sender.Enqueue(message), Is.Zero);
                Assert.That(encoded, Is.EqualTo(64));
                _ = context.Received(1).CloseAsync();
                foreach (IByteBuffer buffer in buffers) Assert.That(buffer.ReferenceCount, Is.Zero);
            }
            finally { sender.HandlerRemoved(context); }
        }

        [Test]
        public async Task Queued_controls_keep_fifo_priority_when_the_codec_is_busy()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage first = new("eth");
            TestMessage second = new("eth");
            TestMessage chunk = new("lean", 1);
            using ManualResetEventSlim started = new();
            using ManualResetEventSlim release = new();
            using ManualResetEventSlim secondReserved = new();
            using DisposableByteBuffer firstBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            using DisposableByteBuffer secondBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            using DisposableByteBuffer chunkBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(first, Arg.Any<IByteBufferAllocator>()).Returns(_ =>
            {
                started.Set();
                Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                return firstBuffer;
            });
            serializer.ZeroSerialize(second, Arg.Any<IByteBufferAllocator>()).Returns(secondBuffer);
            serializer.ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>()).Returns(chunkBuffer);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            Task<int> firstSend = Task.Run(() => sender.Enqueue(first));
            Task<int> secondSend = Task.FromResult(0);
            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                context.Channel.IsWritable.Returns(_ => { secondReserved.Set(); return true; });
                secondSend = Task.Run(() => sender.Enqueue(second));
                Assert.That(secondReserved.Wait(TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(await secondSend.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(4));
                Task<int> bulkSend = sender.EnqueueAsync(chunk, CancellationToken.None).AsTask();
                serializer.Received(1).ZeroSerialize(second, Arg.Any<IByteBufferAllocator>());
                serializer.DidNotReceive().ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>());
                _ = context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
                release.Set();
                await firstSend.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await secondSend.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(4));
                await bulkSend.WaitAsync(TimeSpan.FromSeconds(5));
                Received.InOrder(() =>
                {
                    context.WriteAndFlushAsync(firstBuffer);
                    context.WriteAndFlushAsync(secondBuffer);
                    context.WriteAndFlushAsync(chunkBuffer);
                });
            }
            finally
            {
                release.Set();
                await firstSend.WaitAsync(TimeSpan.FromSeconds(5));
                await secondSend.WaitAsync(TimeSpan.FromSeconds(5));
                sender.HandlerRemoved(context);
            }
        }

        [Test]
        public async Task Close_during_serialization_releases_the_unhanded_buffer([Values] bool asyncChunk)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage message = new(asyncChunk ? "lean" : "eth", asyncChunk ? 1 : 0);
            using ManualResetEventSlim started = new();
            using ManualResetEventSlim release = new();
            IByteBuffer buffer = Unpooled.Buffer(4).WriteZero(4);
            serializer.ZeroSerialize(message, Arg.Any<IByteBufferAllocator>()).Returns(_ =>
            {
                started.Set();
                Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
                return buffer;
            });
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            Task<int> send = Task.Run(async () => asyncChunk
                ? await sender.EnqueueAsync(message, CancellationToken.None)
                : sender.Enqueue(message));
            try
            {
                Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
                await Task.Run(() => sender.HandlerRemoved(context)).WaitAsync(TimeSpan.FromSeconds(5));
                release.Set();
                Assert.That(await send.WaitAsync(TimeSpan.FromSeconds(5)), Is.Zero);
                Assert.That(buffer.ReferenceCount, Is.Zero);
                _ = context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                release.Set();
                await send.WaitAsync(TimeSpan.FromSeconds(5));
                if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        [Test]
        public async Task Serialization_failure_does_not_block_later_control_or_bulk_traffic()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage malformed = new("eth");
            TestMessage control = new("eth");
            TestMessage chunk = new("lean", 1);
            using DisposableByteBuffer controlBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            using DisposableByteBuffer chunkBuffer = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(malformed, Arg.Any<IByteBufferAllocator>()).Returns(_ => throw new InvalidOperationException());
            serializer.ZeroSerialize(control, Arg.Any<IByteBufferAllocator>()).Returns(controlBuffer);
            serializer.ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>()).Returns(chunkBuffer);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            try
            {
                Assert.That(() => sender.Enqueue(malformed), Throws.InstanceOf<InvalidOperationException>());
                Assert.That(sender.Enqueue(control), Is.EqualTo(4));
                Assert.That(await sender.EnqueueAsync(chunk, CancellationToken.None), Is.EqualTo(4));
                Received.InOrder(() =>
                {
                    context.WriteAndFlushAsync(controlBuffer);
                    context.WriteAndFlushAsync(chunkBuffer);
                });
            }
            finally { sender.HandlerRemoved(context); }
        }

        [Test]
        public void Bulk_async_path_requires_explicit_enablement()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage message) = SetupChannel(true);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            try
            {
                Assert.That(async () => await sender.EnqueueAsync(message, CancellationToken.None),
                    Throws.InstanceOf<InvalidOperationException>());
                serializer.DidNotReceive().ZeroSerialize(message, Arg.Any<IByteBufferAllocator>());
            }
            finally { sender.HandlerRemoved(context); }
        }

        [Test]
        public void Backpressure_defers_control_traffic_and_retries_bulk_gossip([Values("eth", "lean")] string controlProtocol)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage lean = new("lean", packetType: 1);
            TestMessage eth = new(controlProtocol);
            IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(32);
            buffer.WriteZero(32);
            serializer.ZeroSerialize(eth, Arg.Any<IByteBufferAllocator>()).Returns(buffer);
            context.Channel.IsWritable.Returns(false);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            try
            {
                Assert.That(sender.Enqueue(lean), Is.Zero);
                Assert.That(sender.Enqueue(eth), Is.EqualTo(32));
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
                serializer.DidNotReceive().ZeroSerialize(lean, Arg.Any<IByteBufferAllocator>());
                context.Channel.IsWritable.Returns(true);
                sender.ChannelWritabilityChanged(context);
                context.Received(1).WriteAndFlushAsync(buffer);
                sender.ChannelWritabilityChanged(context);
                context.Received(1).WriteAndFlushAsync(buffer);
            }
            finally
            {
                sender.HandlerRemoved(context);
                if (buffer.ReferenceCount > 0) buffer.SafeRelease();
            }
        }

        [Test]
        public void Deferred_traffic_has_a_byte_budget_and_is_released_on_close()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            context.Channel.IsWritable.Returns(false);
            TestMessage first = new("eth");
            TestMessage second = new("eth");
            IByteBuffer retained = PooledByteBufferAllocator.Default.Buffer(8 * 1024 * 1024);
            retained.WriteZero(8 * 1024 * 1024);
            IByteBuffer rejected = PooledByteBufferAllocator.Default.Buffer(8 * 1024 * 1024);
            rejected.WriteZero(8 * 1024 * 1024);
            serializer.ZeroSerialize(first, Arg.Any<IByteBufferAllocator>()).Returns(retained);
            serializer.ZeroSerialize(second, Arg.Any<IByteBufferAllocator>()).Returns(rejected);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            try
            {
                Assert.That(sender.Enqueue(first), Is.EqualTo(8 * 1024 * 1024));
                Assert.That(sender.Enqueue(second), Is.Zero);
                Assert.That(rejected.ReferenceCount, Is.Zero);
                sender.HandlerRemoved(context);
                Assert.That(retained.ReferenceCount, Is.Zero);
                Assert.That(sender.Enqueue(first), Is.Zero);
                context.DidNotReceive().WriteAndFlushAsync(Arg.Any<IByteBuffer>());
            }
            finally
            {
                if (retained.ReferenceCount > 0) retained.SafeRelease();
                if (rejected.ReferenceCount > 0) rejected.SafeRelease();
            }
        }

        [Test]
        public async Task Bulk_send_waits_for_actual_write_completion_without_blocking_control_messages()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            TestMessage chunk = new("lean", 1);
            TestMessage control = new("eth");
            using DisposableByteBuffer bulk = Unpooled.Buffer(32).WriteZero(32).AsDisposable();
            using DisposableByteBuffer small = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>()).Returns(bulk);
            serializer.ZeroSerialize(control, Arg.Any<IByteBufferAllocator>()).Returns(small);
            TaskCompletionSource written = new(TaskCreationOptions.RunContinuationsAsynchronously);
            context.WriteAndFlushAsync(bulk).Returns(written.Task);
            context.WriteAndFlushAsync(small).Returns(Task.CompletedTask);
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            try
            {
                Task<int> send = sender.EnqueueAsync(chunk, CancellationToken.None).AsTask();
                Assert.That(send.IsCompleted, Is.False);
                Assert.That(sender.Enqueue(control), Is.EqualTo(4));
                await context.Received(1).WriteAndFlushAsync(small);
                written.SetResult();
                Assert.That(await send.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(32));
            }
            finally { sender.HandlerRemoved(context); }
        }

        [Test]
        public async Task Bulk_waits_unallocated_behind_deferred_control_traffic()
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage _) = SetupChannel(true);
            context.Channel.IsWritable.Returns(false);
            TestMessage chunk = new("lean", 1);
            TestMessage control = new("eth");
            using DisposableByteBuffer bulk = Unpooled.Buffer(32).WriteZero(32).AsDisposable();
            using DisposableByteBuffer small = Unpooled.Buffer(4).WriteZero(4).AsDisposable();
            serializer.ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>()).Returns(bulk);
            serializer.ZeroSerialize(control, Arg.Any<IByteBufferAllocator>()).Returns(small);
            TaskCompletionSource controlWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
            context.WriteAndFlushAsync(small).Returns(_ => { controlWritten.SetResult(); return Task.CompletedTask; });
            context.WriteAndFlushAsync(bulk).Returns(_ =>
            {
                Assert.That(controlWritten.Task.IsCompletedSuccessfully, Is.True);
                return Task.CompletedTask;
            });
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            try
            {
                Task<int> send = sender.EnqueueAsync(chunk, CancellationToken.None).AsTask();
                serializer.DidNotReceive().ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>());
                Assert.That(sender.Enqueue(control), Is.EqualTo(4));
                context.Channel.IsWritable.Returns(true);
                sender.ChannelWritabilityChanged(context);
                Assert.That(await send.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(32));
            }
            finally { sender.HandlerRemoved(context); }
        }

        [Test]
        public async Task Cancelled_or_closed_bulk_waiter_never_serializes([Values] bool close)
        {
            (IChannelHandlerContext context, IMessageSerializationService serializer, TestMessage chunk) = SetupChannel(true);
            context.Channel.IsWritable.Returns(false);
            using CancellationTokenSource cancellation = new();
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            sender.HandlerAdded(context);
            sender.EnableLeanBulk();
            Task<int> send = sender.EnqueueAsync(chunk, cancellation.Token).AsTask();
            try
            {
                if (close)
                {
                    sender.HandlerRemoved(context);
                    Assert.That(await send.WaitAsync(TimeSpan.FromSeconds(5)), Is.Zero);
                }
                else
                {
                    cancellation.Cancel();
                    Assert.That(async () => await send, Throws.InstanceOf<OperationCanceledException>());
                }
                serializer.DidNotReceive().ZeroSerialize(chunk, Arg.Any<IByteBufferAllocator>());
            }
            finally { if (!close) sender.HandlerRemoved(context); }
        }

        private class TestMessage(string protocol = "", int packetType = 0) : P2PMessage
        {
            public override int PacketType { get; } = packetType;
            public override string Protocol { get; } = protocol;
        }
    }
}
