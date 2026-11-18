// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Threading;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class SessionMonitorTests
    {
        private IPingSender _pingSender;

        private IPingSender _noPong;

        [SetUp]
        public void SetUp()
        {
            _pingSender = Substitute.For<IPingSender>();
            _pingSender.SendPing().Returns(Task.FromResult(true));

            _noPong = Substitute.For<IPingSender>();
            _noPong.SendPing().Returns(Task.FromResult(false));
        }

        [Test]
        public void Can_remove_session()
        {
            ISession session = CreateSession();
            SessionMonitor sessionMonitor = new(new NetworkConfig(), LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            sessionMonitor.RemoveSession(session);

            Assert.That(sessionMonitor.Sessions, Is.Empty);
        }

        [Test]
        [Explicit("Travis fails here")]
        public async Task Will_keep_pinging()
        {
            ISession session1 = CreateSession();
            ISession session2 = CreateUnresponsiveSession();

            NetworkConfig networkConfig = new();
            networkConfig.P2PPingInterval = 50;
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(session1);
            sessionMonitor.AddSession(session2);
            sessionMonitor.Start();
            await Task.Delay(300);
            sessionMonitor.Stop();

            await _pingSender.Received().SendPing();
            await _noPong.Received().SendPing();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(session1.State, Is.EqualTo(SessionState.Initialized));
                Assert.That(session2.State, Is.EqualTo(SessionState.Disconnected));
            }
        }

        [Test]
        public async Task Disconnects_a_session_that_misses_every_pong_and_reports_a_timeout()
        {
            ISession responsive = CreateSession();
            ISession unresponsive = CreateUnresponsiveSession();
            DisconnectReason? reason = null;
            DateTime disconnectedAt = default;
            unresponsive.Disconnected += (_, args) =>
            {
                disconnectedAt = DateTime.UtcNow;
                reason = args.DisconnectReason;
            };

            NetworkConfig networkConfig = new();
            networkConfig.P2PPingInterval = 200;
            TestLogger logger = new() { IsDebug = false };
            SessionMonitor sessionMonitor = new(networkConfig, new OneLoggerLogManager(new(logger)));
            sessionMonitor.AddSession(responsive);
            sessionMonitor.AddSession(unresponsive);
            DateTime lastPong = DateTime.UtcNow;
            responsive.LastPongUtc = lastPong;
            unresponsive.LastPongUtc = lastPong;
            sessionMonitor.Start();
            try
            {
                Assert.That(() => unresponsive.IsClosing, Is.True.After(10_000, 20));
                Assert.That(() => reason, Is.EqualTo(DisconnectReason.ReceiveMessageTimeout).After(5_000, 20),
                    "the reason selects the reconnect delay and lets the privileged-node gate reap dead static sessions");
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(disconnectedAt - lastPong, Is.GreaterThanOrEqualTo(6 * TimeSpan.FromMilliseconds(networkConfig.P2PPingInterval)),
                        "the window is measured from the last pong, so the first missed pongs have to be tolerated");
                    Assert.That(responsive.IsClosing, Is.False);
                }
            }
            finally
            {
                sessionMonitor.Stop();
            }

            Assert.That(logger.LogList, Has.Some.Contains("No pong received in response"));
        }

        [Test]
        public async Task Keeps_a_session_that_recovers_between_missed_pongs()
        {
            int calls = 0;
            IPingSender flaky = Substitute.For<IPingSender>();
            flaky.SendPing().Returns(_ => Task.FromResult(Interlocked.Increment(ref calls) % 3 == 0));
            ISession session = CreateSession(flaky);

            NetworkConfig networkConfig = new();
            networkConfig.P2PPingInterval = 500;
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);
            sessionMonitor.AddSession(session);
            sessionMonitor.Start();
            try
            {
                await Task.Delay(4_000);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(Volatile.Read(ref calls), Is.GreaterThanOrEqualTo(3),
                        "the recovery path is only exercised once the third ping has returned a pong");
                    Assert.That(session.IsClosing, Is.False,
                        "two missed pongs followed by a pong is a slow peer, not a dead one");
                }
            }
            finally
            {
                sessionMonitor.Stop();
            }
        }

        private ISession CreateSession() => CreateSession(_pingSender);

        private ISession CreateSession(IPingSender pingSender, IChannel? channel = null)
        {
            if (channel is null)
            {
                channel = Substitute.For<IChannel>();
                channel.IsWritable.Returns(true);
            }
            ISession session = new Session(30312, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
            session.RemoteHost = "1.2.3.4";
            session.RemotePort = 12345;
            session.PingSender = pingSender;
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, Substitute.For<IChannelHandlerContext>(), Substitute.For<IPacketSender>());
            return session;
        }

        [Test]
        public async Task Unsolicited_pongs_do_not_prevent_timeout()
        {
            ManualTimeProvider time = new();
            ISession session = CreateSession(_noPong);
            using P2PProtocolHandler handler = new(session,
                new Enode(TestItem.PublicKeyA, IPAddress.Loopback, 30303),
                Substitute.For<INodeStatsManager>(), Substitute.For<IMessageSerializationService>(),
                Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance, new NetworkConfig(), time);
            using MonitorHarness monitor = new(session, time);
            Packet pong = new([]) { Protocol = Protocol.P2P, PacketType = P2PMessageCode.Pong };

            for (int i = 0; i < 4; i++)
            {
                time.Advance(TimeSpan.FromSeconds(2));
                handler.HandleMessage(pong);
                await monitor.Tick(TimeSpan.Zero);
            }

            Assert.That(session.IsClosing, Is.True);
        }

        [Test]
        public async Task Persistent_non_writability_times_out_independently_of_pongs([Values] bool recovers)
        {
            ManualTimeProvider time = new();
            IChannel channel = Substitute.For<IChannel>();
            channel.IsWritable.Returns(false);
            ISession session = CreateSession(_pingSender, channel);
            DisconnectReason? reason = null;
            session.Disconnected += (_, args) => reason = args.DisconnectReason;
            using MonitorHarness monitor = new(session, time);

            await monitor.Tick(TimeSpan.FromSeconds(2));
            await monitor.Tick(TimeSpan.FromSeconds(29));
            Assert.That(session.IsClosing, Is.False);
            if (recovers)
            {
                channel.IsWritable.Returns(true);
                await monitor.Tick(TimeSpan.FromSeconds(2));
                channel.IsWritable.Returns(false);
                await monitor.Tick(TimeSpan.FromSeconds(2));
                await monitor.Tick(TimeSpan.FromSeconds(29));
                Assert.That(session.IsClosing, Is.False);
            }

            await monitor.Tick(TimeSpan.FromSeconds(2));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.IsClosing, Is.True);
                Assert.That(reason, Is.EqualTo(DisconnectReason.ReceiveMessageTimeout));
            }
        }

        private sealed class MonitorHarness : IDisposable
        {
            private readonly ManualTimeProvider _time;
            private readonly SessionMonitor _monitor;
            private readonly ISession _session;
            private TaskCompletionSource _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public MonitorHarness(ISession session, ManualTimeProvider time)
            {
                _time = time;
                _session = session;
                InterfaceLogger logger = Substitute.For<InterfaceLogger>();
                logger.IsTrace.Returns(true);
                logger.When(l => l.Trace(Arg.Is<string>(s => s.StartsWith("Sent "))))
                    .Do(_ => _tick.TrySetResult());
                ILogManager logs = new OneLoggerLogManager(new(logger));
                _monitor = new SessionMonitor(new NetworkConfig { P2PPingInterval = 1000 }, logs, time);
                _monitor.AddSession(session);
                session.LastPongUtc = time.GetUtcNow().UtcDateTime;
                _monitor.Start();
            }

            public async Task Tick(TimeSpan elapsed)
            {
                _tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _time.AdvanceAndFireTimer(elapsed);
                await _tick.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }

            public void Dispose()
            {
                _monitor.Stop();
                if (!_session.IsClosing) _session.MarkDisconnected(DisconnectReason.ClientQuitting, DisconnectType.Remote, "test cleanup");
                _session.Dispose();
            }
        }

        [Test]
        public async Task Persistent_backpressure_closes_channel_and_releases_queued_buffer()
        {
            ManualTimeProvider time = new();
            IMessageSerializationService serializer = new MessageSerializationService(SerializerInfo.Create(new PingMessageSerializer()));
            PacketSender sender = new(serializer, LimboLogs.Instance, TimeSpan.Zero);
            NonReadingChannel channel = new(sender);
            channel.Configuration.WriteBufferLowWaterMark = 0;
            channel.Configuration.WriteBufferHighWaterMark = 1;
            Session session = new(30312, channel, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
            {
                RemoteHost = "1.2.3.4",
                RemotePort = 12345,
                PingSender = _pingSender,
            };
            session.Handshake(TestItem.PublicKeyB);
            session.Init(5, channel.Pipeline.Context(sender), sender);
            try
            {
                sender.Enqueue(PingMessage.Instance);
                Assert.That(channel.IsWritable, Is.False);
                IByteBuffer buffer = channel.PendingBuffer!;
                Assert.That(buffer.ReferenceCount, Is.EqualTo(1));
                using MonitorHarness monitor = new(session, time);
                await monitor.Tick(TimeSpan.FromSeconds(2));
                await monitor.Tick(TimeSpan.FromSeconds(31));

                Assert.That(session.IsClosing, Is.True);
                Assert.That(() =>
                {
                    channel.RunPendingTasks();
                    return channel.Open;
                }, Is.False.After(5000, 10));
                Assert.That(buffer.ReferenceCount, Is.Zero);
            }
            finally
            {
                await channel.CloseAsync();
                channel.FinishAndReleaseAll();
            }
        }

        private sealed class NonReadingChannel(params IChannelHandler[] handlers) : EmbeddedChannel(handlers)
        {
            public IByteBuffer? PendingBuffer { get; private set; }
            protected override void DoWrite(ChannelOutboundBuffer input) => PendingBuffer = input.Current as IByteBuffer;
        }

        [Test]
        public void AddSession_Staggers_Ping_Times()
        {
            NetworkConfig networkConfig = new() { P2PPingInterval = 10_000 };
            SessionMonitor sessionMonitor = new(networkConfig, LimboLogs.Instance);

            const int sessionCount = 20;
            ISession[] sessions = new ISession[sessionCount];
            for (int i = 0; i < sessionCount; i++)
            {
                sessions[i] = CreateSession();
                sessionMonitor.AddSession(sessions[i]);
            }

            // Sessions should have different LastPingUtc values due to jitter
            DateTime[] pingTimes = sessions.Select(s => s.LastPingUtc).ToArray();
            int distinctCount = pingTimes.Distinct().Count();
            Assert.That(distinctCount, Is.GreaterThan(1), "Sessions added at the same time should have staggered ping times");

            // The spread should cover a meaningful portion of the interval
            TimeSpan spread = pingTimes.Max() - pingTimes.Min();
            Assert.That(spread, Is.GreaterThan(TimeSpan.FromMilliseconds(100)), "Ping time spread should be non-trivial");
        }

        private ISession CreateUnresponsiveSession() => CreateSession(_noPong);
    }
}
