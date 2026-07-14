// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KademliaAdapterTests
    {
        public enum NoResponseRequest
        {
            Ping,
            FindNeighbours,
            SendEnrRequest
        }

        /// <summary>
        /// Request timeout for tests whose mocks answer inline, where timeouts act only as a failsafe.
        /// </summary>
        /// <remarks>
        /// Must stay well above CI scheduling jitter: a timed-out request can be cancelled before it is even sent,
        /// and a timed-out ENR refresh is skipped silently, flaking the refresh assertions on slow runners.
        /// </remarks>
        private const int FailsafeRequestTimeoutMs = 10_000;

        /// <summary>
        /// Request timeout for tests that assert timeout behavior and need requests to expire quickly.
        /// </summary>
        private const int ExpiringRequestTimeoutMs = 100;

        private IKademliaAdapter _adapter = null!;

        private IKademlia<PublicKey, Node> _kademliaMessageReceiver = null!;
        private INodeHealthTracker<Node> _nodeHealthTracker = null!;
        private INetworkConfig _networkConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;
        private NodeRecord _selfNodeRecord = null!;
        private ILogManager _logManager = null!;
        private ITimestamper _timestamper = null!;
        private IMsgSender _msgSender = null!;
        private INodeStatsManager _nodeStatsManager = null!;
        private INodeRecordProvider _nodeRecordProvider = null!;
        private Node _testNode = null!;
        private PublicKey _testPublicKey = null!;

        private IMessageSerializationService _receiverSerializationManager;
        private Node _receiver;

        private void ConfigureBondCallback(IPEndPoint? pongFarAddress = null, ulong? pongEnrSequence = null) =>
            _msgSender
                .SendMsg(Arg.Any<PingMsg>())
                .Returns(ci =>
                {
                    PingMsg sent = (PingMsg)ci[0]!;
                    using DisposableByteBuffer buffer = _receiverSerializationManager.ZeroSerialize(sent).AsDisposable();
                    PingMsg msg = _receiverSerializationManager.Deserialize<PingMsg>(buffer);
                    PongMsg pong = new(
                        msg.FarPublicKey!,
                        _timestamper.UnixTime.SecondsLong + 1,
                        sent.Mdc!.Value,
                        pongEnrSequence);
                    pong.FarAddress = pongFarAddress ?? sent.FarAddress;
                    return _adapter.OnIncomingMsg(pong);
                });

        private async Task BondReceiver(CancellationToken token)
        {
            ConfigureBondCallback();
            await _adapter.Ping(_receiver, token);
            _msgSender.ClearReceivedCalls();
            _nodeHealthTracker.ClearReceivedCalls();
        }

        [SetUp]
        public void Setup()
        {
            // test node & dependencies
            _testPublicKey = TestItem.PublicKeyA;
            _testNode = new(_testPublicKey, "192.168.1.1", 30303);

            _kademliaMessageReceiver = Substitute.For<IKademlia<PublicKey, Node>>();
            _nodeHealthTracker = Substitute.For<INodeHealthTracker<Node>>();
            _networkConfig = Substitute.For<INetworkConfig>();
            _networkConfig.MaxActivePeers.Returns(25);
            _kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = _testNode };

            _selfNodeRecord = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyA,
                IPAddress.Parse("192.168.1.1"),
                tcpPort: _networkConfig.P2PPort,
                udpPort: _networkConfig.DiscoveryPort);

            _logManager = LimboLogs.Instance;
            _timestamper = Substitute.For<ITimestamper>();
            DateTime now = new(2021, 5, 3, 0, 0, 0, DateTimeKind.Utc);
            _timestamper.UtcNow.Returns(now);
            _timestamper.UnixTime.Returns(new UnixTime(now));
            _msgSender = Substitute.For<IMsgSender>();
            _msgSender.SendMsg(Arg.Any<DiscoveryMsg>()).Returns(Task.CompletedTask);

            _receiver = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
            SerializationBuilder builder = new();
            builder.WithDiscovery(TestItem.PrivateKeyB);
            _receiverSerializationManager = builder.TestObject;

            _nodeRecordProvider = Substitute.For<INodeRecordProvider>();
            _nodeRecordProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<NodeRecord>(_selfNodeRecord));
            _nodeStatsManager = Substitute.For<INodeStatsManager>();
            _nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(Substitute.For<INodeStats>());

            _adapter = CreateAdapter(FailsafeRequestTimeoutMs);
        }

        private KademliaAdapter CreateAdapter(int requestTimeoutMs) => new(
            new Lazy<IKademlia<PublicKey, Node>>(() => _kademliaMessageReceiver),
            new Lazy<INodeHealthTracker<Node>>(() => _nodeHealthTracker),
            new DiscoveryConfig
            {
                EnrTimeout = requestTimeoutMs,
                PingTimeout = requestTimeoutMs,
                SendNodeTimeout = requestTimeoutMs,
                BondWaitTime = 1,
            },
            _kademliaConfig,
            _nodeRecordProvider,
            _nodeStatsManager,
            _timestamper,
            Substitute.For<IProcessExitSource>(),
            new Ecdsa(),
            _logManager)
        {
            MsgSender = _msgSender,
        };

        private async Task UseExpiringRequestTimeouts()
        {
            await _adapter.DisposeAsync();
            _adapter = CreateAdapter(ExpiringRequestTimeoutMs);
        }

        [Test]
        public async Task GetSession_should_return_single_session_for_concurrent_calls()
        {
            Node[] nodes = Enumerable.Repeat(_receiver, 128).ToArray();

            NodeSession[] sessions = await Task.WhenAll(nodes.Select(node => Task.Run(() => _adapter.GetSession(node))));

            Assert.That(sessions.All(session => ReferenceEquals(session, sessions[0])), Is.True);
            _nodeStatsManager.Received(1).GetOrAdd(Arg.Is<Node>(node => node.Id == _receiver.Id));
        }

        [TearDown]
        public async Task TearDown() => await _adapter.DisposeAsync();

        private T AddReceiverFarAddress<T>(T msg) where T : DiscoveryMsg
        {
            using DisposableByteBuffer buffer = _receiverSerializationManager.ZeroSerialize<T>(msg).AsDisposable();
            IPEndPoint? farAddress = msg.FarAddress;
            msg = _receiverSerializationManager.Deserialize<T>(buffer);
            msg.FarAddress = farAddress;
            return msg;
        }

        private async Task<bool> HasResponse(NoResponseRequest request, CancellationToken token) =>
            request switch
            {
                NoResponseRequest.Ping => await _adapter.Ping(_receiver, token),
                NoResponseRequest.FindNeighbours => await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token) is not null,
                NoResponseRequest.SendEnrRequest => await _adapter.SendEnrRequest(_receiver, token) is not null,
                _ => throw new ArgumentOutOfRangeException(nameof(request), request, null)
            };

        private async Task<Node> ReadPeerCandidate(CancellationToken token)
        {
            await using IAsyncEnumerator<Node> enumerator = _adapter
                .ReadPeerCandidates(token)
                .GetAsyncEnumerator(token);
            Assert.That(await enumerator.MoveNextAsync(), Is.True);
            return enumerator.Current;
        }

        private async Task AssertNoPeerCandidate(CancellationToken token)
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(100));
            await using IAsyncEnumerator<Node> enumerator = _adapter
                .ReadPeerCandidates(timeoutCts.Token)
                .GetAsyncEnumerator(timeoutCts.Token);

            try
            {
                Assert.That(await enumerator.MoveNextAsync(), Is.False);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
            }
        }

        private DiscoveryMsg CreateUnsolicitedResponse(MsgType msgType) =>
            msgType switch
            {
                MsgType.Pong => AddReceiverFarAddress(new PongMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, TestItem.KeccakA.ValueHash256)),
                MsgType.Neighbors => AddReceiverFarAddress(new NeighborsMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, Array.Empty<Node>())),
                MsgType.EnrResponse => AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, _selfNodeRecord, TestItem.KeccakA)),
                _ => throw new ArgumentOutOfRangeException(nameof(msgType), msgType, null)
            };

        private NodeRecord ConfigureRemoteEnrRefresh(
            ulong? advertisedSequence,
            ulong responseSequence,
            int? tcpPort = null)
        {
            NodeRecord remoteRecord = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyB,
                IPAddress.Parse("192.168.1.2"),
                tcpPort: tcpPort,
                udpPort: 30303,
                enrSequence: responseSequence);

            _msgSender
                .SendMsg(Arg.Any<PingMsg>())
                .Returns(ci =>
                {
                    PingMsg sent = (PingMsg)ci[0]!;
                    using DisposableByteBuffer buffer = _receiverSerializationManager.ZeroSerialize(sent).AsDisposable();
                    PingMsg msg = _receiverSerializationManager.Deserialize<PingMsg>(buffer);
                    PongMsg pong = new(
                        msg.FarPublicKey!,
                        _timestamper.UnixTime.SecondsLong + 1,
                        sent.Mdc!.Value,
                        advertisedSequence);
                    pong.FarAddress = sent.FarAddress;
                    return _adapter.OnIncomingMsg(pong);
                });

            _msgSender
                .SendMsg(Arg.Any<EnrRequestMsg>())
                .Returns(ci =>
                {
                    EnrRequestMsg sent = (EnrRequestMsg)ci[0]!;
                    ValueHash256 requestHash = TestItem.KeccakA.ValueHash256;
                    sent.Hash = requestHash;
                    EnrResponseMsg response = AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, remoteRecord, new Hash256(requestHash)));
                    return _adapter.OnIncomingMsg(response);
                });

            return remoteRecord;
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_send_ping_and_receive_pong(CancellationToken token)
        {
            ConfigureBondCallback();

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_not_publish_tcp_endpoint_learned_from_neighbours(CancellationToken token)
        {
            ConfigureBondCallback();

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            await AssertNoPeerCandidate(token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_publish_tcp_endpoint_from_verified_enr(CancellationToken token)
        {
            NodeRecord remoteRecord = ConfigureRemoteEnrRefresh(2, 2, tcpPort: 30304);

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            Node peerNode = await ReadPeerCandidate(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peerNode.Id, Is.EqualTo(_receiver.Id));
                Assert.That(peerNode.Port, Is.EqualTo(30304));
                Assert.That(peerNode.DiscoveryPort, Is.EqualTo(30303));
                Assert.That(peerNode.Enr.GetHex(), Is.EqualTo(remoteRecord.GetHex()));
                Assert.That(peerNode, Is.Not.SameAs(_receiver));
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_not_publish_tcp_endpoint_from_forged_enr(CancellationToken token)
        {
            NodeRecord forgedRecord = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyB,
                IPAddress.Parse("192.168.1.2"),
                tcpPort: 30304,
                udpPort: 30303,
                enrSequence: 1);
            Signature validSignature = forgedRecord.Signature!;
            forgedRecord.SetEntry(new TcpEntry(30305));
            forgedRecord.Signature = validSignature;
            _receiver.Enr = forgedRecord;
            ConfigureBondCallback();

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            await AssertNoPeerCandidate(token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_publish_only_refreshed_enr_candidate(CancellationToken token)
        {
            _receiver.Enr = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyB,
                IPAddress.Parse("192.168.1.2"),
                tcpPort: 30303,
                udpPort: 30303,
                enrSequence: 1);
            NodeRecord refreshedRecord = ConfigureRemoteEnrRefresh(2, 2, tcpPort: 30304);

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            Node peerNode = await ReadPeerCandidate(token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(peerNode.Port, Is.EqualTo(30304));
                Assert.That(peerNode.Enr.EnrSequence, Is.EqualTo(2));
                Assert.That(peerNode.Enr.GetHex(), Is.EqualTo(refreshedRecord.GetHex()));
            }

            await AssertNoPeerCandidate(token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_not_publish_stale_enr_when_refresh_fails(CancellationToken token)
        {
            await UseExpiringRequestTimeouts();
            _receiver.Enr = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyB,
                IPAddress.Parse("192.168.1.2"),
                tcpPort: 30303,
                udpPort: 30303,
                enrSequence: 1);
            ConfigureBondCallback(pongEnrSequence: 2);

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            await AssertNoPeerCandidate(token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_not_publish_verified_enr_without_tcp_endpoint(CancellationToken token)
        {
            _ = ConfigureRemoteEnrRefresh(2, 2);

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            await AssertNoPeerCandidate(token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Ping_should_not_bond_requested_endpoint_when_pong_source_differs(CancellationToken token)
        {
            IPEndPoint pongFarAddress = new(IPAddress.Parse("192.168.1.4"), _receiver.Address.Port);
            ConfigureBondCallback(pongFarAddress, pongEnrSequence: 42);

            bool result = await _adapter.Ping(_receiver, token);
            Assert.That(result, Is.False);
            await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrRequestMsg>());
            _msgSender.ClearReceivedCalls();

            FindNodeMsg findNodeMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            findNodeMsg = AddReceiverFarAddress(findNodeMsg);

            Node[] expectedNodes = [new(TestItem.PublicKeyD, "192.168.1.3", 30303)];
            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Any<PublicKey>(),
                Arg.Any<Node>())
                .Returns(expectedNodes);

            await _adapter.OnIncomingMsg(findNodeMsg);

            await _msgSender.DidNotReceive().SendMsg(Arg.Any<NeighborsMsg>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task FindNeighbours_should_return_nodes(CancellationToken token)
        {
            Node[] expected = Enumerable.Repeat(new Node(TestItem.PublicKeyD, "192.168.1.3", 30303), 16).ToArray();

            ConfigureBondCallback();

            _msgSender
                .When(x => x.SendMsg(Arg.Any<FindNodeMsg>()))
                .Do(ci =>
                {
                    ArraySegment<Node> neighbours1 = expected[..12];

                    NeighborsMsg neighbors = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, neighbours1);
                    neighbors = AddReceiverFarAddress(neighbors);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors));

                    ArraySegment<Node> neighbours2 = expected[12..];
                    NeighborsMsg neighbors2 = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, neighbours2);
                    neighbors2 = AddReceiverFarAddress(neighbors2);
                    Task.Run(() => _adapter.OnIncomingMsg(neighbors2));
                });

            Node[]? result = await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token);
            Assert.That(result, Is.EquivalentTo(expected));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task SendEnrRequest_should_ping_then_enr_request_and_return_response(CancellationToken token)
        {
            ConfigureBondCallback();

            ValueHash256 requestHash = TestItem.KeccakA.ValueHash256;
            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(ci =>
                {
                    EnrRequestMsg sent = (EnrRequestMsg)ci[0]!;
                    sent.Hash = requestHash;
                    EnrResponseMsg response = AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, _selfNodeRecord, new Hash256(requestHash)));
                    Task.Run(() => _adapter.OnIncomingMsg(response));
                });

            EnrResponseMsg? result = await _adapter.SendEnrRequest(_receiver, token);

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            Assert.That(result?.NodeRecord.GetHex(), Is.EqualTo(_selfNodeRecord.GetHex()));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task SendEnrRequest_should_reject_unsolicited_response_with_wrong_keccak(CancellationToken token)
        {
            await UseExpiringRequestTimeouts();
            ConfigureBondCallback();

            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(ci =>
                {
                    EnrRequestMsg sent = (EnrRequestMsg)ci[0]!;
                    sent.Hash = TestItem.KeccakA.ValueHash256;
                    EnrResponseMsg response = AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, _selfNodeRecord, TestItem.KeccakB));
                    Task.Run(() => _adapter.OnIncomingMsg(response));
                });

            EnrResponseMsg? result = await _adapter.SendEnrRequest(_receiver, token);

            Assert.That(result, Is.Null);
        }

        private static IEnumerable<TestCaseData> RemoteEnrRefreshCases()
        {
            yield return new TestCaseData(0UL, 0UL, false, false)
                .SetName("Ping_should_not_request_remote_enr_when_pong_has_no_advertised_sequence");
            yield return new TestCaseData(2UL, 2UL, true, true)
                .SetName("Ping_should_cache_remote_enr_when_response_sequence_matches_advertised_sequence");
            yield return new TestCaseData(3UL, 2UL, true, false)
                .SetName("Ping_should_not_cache_remote_enr_when_response_sequence_is_below_advertised_sequence");
            yield return new TestCaseData(3UL, 4UL, true, true)
                .SetName("Ping_should_cache_remote_enr_when_response_sequence_is_above_advertised_sequence");
        }

        [TestCaseSource(nameof(RemoteEnrRefreshCases))]
        [CancelAfter(10000)]
        public async Task Ping_should_refresh_remote_enr_from_advertised_sequence(
            ulong advertisedSequence,
            ulong responseSequence,
            bool shouldRequestEnr,
            bool shouldCacheEnr,
            CancellationToken token)
        {
            NodeRecord remoteRecord = ConfigureRemoteEnrRefresh(advertisedSequence, responseSequence);

            bool result = await _adapter.Ping(_receiver, token);

            Assert.That(result, Is.True);
            if (shouldRequestEnr)
            {
                await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            }
            else
            {
                await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrRequestMsg>());
            }

            if (shouldCacheEnr)
            {
                _kademliaMessageReceiver.Received(1).AddOrRefresh(Arg.Is<Node>(n =>
                    HasNodeRecord(n, _receiver, remoteRecord)));
            }
            else
            {
                _kademliaMessageReceiver.DidNotReceive().AddOrRefresh(Arg.Any<Node>());
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Timed_out_response_handler_should_not_consume_later_unsolicited_message(CancellationToken token)
        {
            await UseExpiringRequestTimeouts();
            ConfigureBondCallback();

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address);
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);
            await _adapter.OnIncomingMsg(pingMsg);

            EnrResponseMsg? result = await _adapter.SendEnrRequest(_receiver, token);

            Assert.That(result, Is.Null);

            _nodeHealthTracker.ClearReceivedCalls();

            EnrResponseMsg response = new(
                _receiver.Address,
                _selfNodeRecord,
                new(new byte[32]));
            response = AddReceiverFarAddress(response);

            await _adapter.OnIncomingMsg(response);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
        }

        [TestCase(MsgType.Pong)]
        [TestCase(MsgType.Neighbors)]
        [TestCase(MsgType.EnrResponse)]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_unsolicited_response_should_not_create_session_stats(MsgType msgType)
        {
            DiscoveryMsg response = CreateUnsolicitedResponse(msgType);

            await _adapter.OnIncomingMsg(response);

            _nodeStatsManager.DidNotReceive().GetOrAdd(Arg.Any<Node>());
            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Any<Node>());
        }

        [TestCase(NoResponseRequest.Ping)]
        [TestCase(NoResponseRequest.FindNeighbours)]
        [TestCase(NoResponseRequest.SendEnrRequest)]
        [CancelAfter(10000)]
        public async Task Request_timeout_should_return_no_response_and_record_failure_once(NoResponseRequest request, CancellationToken token)
        {
            await UseExpiringRequestTimeouts();
            if (request is not NoResponseRequest.Ping)
            {
                ConfigureBondCallback();
            }

            bool hasResponse = await HasResponse(request, token);

            Assert.That(hasResponse, Is.False);
            _nodeHealthTracker.Received(1).OnRequestFailed(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task FindNeighbours_should_not_send_find_node_when_bond_ping_times_out(CancellationToken token)
        {
            await UseExpiringRequestTimeouts();

            Node[]? result = await _adapter.FindNeighbours(_receiver, TestItem.PublicKeyC, token);

            Assert.That(result, Is.Null);
            await _msgSender.Received(1).SendMsg(Arg.Is<DiscoveryMsg>(m => m is PingMsg));
            await _msgSender.DidNotReceive().SendMsg(Arg.Is<DiscoveryMsg>(m => m is FindNodeMsg));
        }

        [Test]
        [CancelAfter(10000)]
        public void Ping_should_throw_on_lifecycle_cancellation(CancellationToken token)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () => await _adapter.Ping(_receiver, cts.Token));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Failed_send_should_remove_response_handler(CancellationToken token)
        {
            PingMsg? sent = null;
            _msgSender.SendMsg(Arg.Any<PingMsg>()).Returns(callInfo =>
            {
                sent = (PingMsg)callInfo[0]!;
                return Task.FromException(new InvalidOperationException("send failed"));
            });

            Assert.ThrowsAsync<InvalidOperationException>(async () => await _adapter.Ping(_receiver, token));
            Assert.That(sent, Is.Not.Null);
            sent = AddReceiverFarAddress(sent!);

            _nodeHealthTracker.ClearReceivedCalls();

            PongMsg response = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, sent!.Mdc!.Value);
            response = AddReceiverFarAddress(response);
            await _adapter.OnIncomingMsg(response);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_respond_with_pong(CancellationToken token)
        {
            ConfigureBondCallback();

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address);
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            ValueHash256 expectedPingMdc = pingMsg.Mdc!.Value;
            await _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.PingMdc == expectedPingMdc));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_use_advertised_tcp_port(CancellationToken token)
        {
            ConfigureBondCallback();
            IPEndPoint discoveryEndpoint = new(_receiver.Address.Address, 30304);
            PingMsg pingMsg = new(discoveryEndpoint, _timestamper.UnixTime.SecondsLong + 20, discoveryEndpoint, 30303, 0);
            pingMsg.FarAddress = discoveryEndpoint;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            _nodeHealthTracker.Received(1).OnIncomingMessageFrom(Arg.Is<Node>(n =>
                n.Id == _receiver.Id &&
                n.Port == 30303 &&
                n.DiscoveryPort == 30304));
        }

        [TestCase(30303, true)]
        [TestCase(0, false)]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_only_publish_dialable_self_reported_endpoint(
            int advertisedTcpPort,
            bool shouldPublish,
            CancellationToken token)
        {
            ConfigureBondCallback();
            IPEndPoint discoveryEndpoint = new(_receiver.Address.Address, 30304);
            PingMsg pingMsg = new(
                discoveryEndpoint,
                _timestamper.UnixTime.SecondsLong + 20,
                discoveryEndpoint,
                advertisedTcpPort,
                0)
            {
                FarAddress = discoveryEndpoint
            };
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            if (shouldPublish)
            {
                Node peerNode = await ReadPeerCandidate(token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(peerNode.Id, Is.EqualTo(_receiver.Id));
                    Assert.That(peerNode.Port, Is.EqualTo(advertisedTcpPort));
                    Assert.That(peerNode.DiscoveryAddress, Is.EqualTo(discoveryEndpoint));
                }
            }
            else
            {
                await AssertNoPeerCandidate(token);
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_not_publish_when_exact_endpoint_bond_fails(CancellationToken token)
        {
            IPEndPoint discoveryEndpoint = new(_receiver.Address.Address, 30304);
            ConfigureBondCallback(new IPEndPoint(IPAddress.Parse("192.168.1.9"), discoveryEndpoint.Port));
            PingMsg pingMsg = new(
                discoveryEndpoint,
                _timestamper.UnixTime.SecondsLong + 20,
                discoveryEndpoint,
                30303,
                0)
            {
                FarAddress = discoveryEndpoint
            };
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            await AssertNoPeerCandidate(token);
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_should_refresh_enr_when_bonding_pong_omits_sequence(CancellationToken token)
        {
            NodeRecord remoteRecord = ConfigureRemoteEnrRefresh(null, 42, tcpPort: 30303);

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address)
            {
                EnrSequence = 42
            };
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            await _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            await _msgSender.Received(1).SendMsg(Arg.Any<EnrRequestMsg>());
            _kademliaMessageReceiver.Received(1).AddOrRefresh(Arg.Is<Node>(n =>
                HasNodeRecord(n, _receiver, remoteRecord)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_from_bonded_peer_should_refresh_remote_enr(CancellationToken token)
        {
            await BondReceiver(token);
            NodeRecord remoteRecord = ConfigureRemoteEnrRefresh(42, 42);

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address)
            {
                EnrSequence = 42
            };
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrRequestMsg>(m => m.FarAddress!.Equals(_receiver.Address)));
            _kademliaMessageReceiver.Received(1).AddOrRefresh(Arg.Is<Node>(n =>
                HasNodeRecord(n, _receiver, remoteRecord)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_ping_from_bonded_node_at_unbonded_endpoint_should_send_bonding_ping(CancellationToken token)
        {
            ConfigureBondCallback();
            await BondReceiver(token);
            _msgSender.ClearReceivedCalls();
            NodeRecord remoteRecord = ConfigureRemoteEnrRefresh(null, 42, tcpPort: 30303);

            IPEndPoint differentEndpoint = new(IPAddress.Parse("192.168.1.3"), _receiver.Address.Port);
            PingMsg pingMsg = new(differentEndpoint, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address)
            {
                EnrSequence = 42
            };
            pingMsg.FarAddress = differentEndpoint;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            await _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m => m.FarAddress!.Equals(differentEndpoint)));
            await _msgSender.Received(1).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Equals(differentEndpoint)));
            await _msgSender.Received(1).SendMsg(Arg.Any<EnrRequestMsg>());
            _kademliaMessageReceiver.Received(1).AddOrRefresh(Arg.Is<Node>(n =>
                HasNodeRecord(n, _receiver, remoteRecord)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_find_node_should_respond_with_neighbors(CancellationToken token)
        {
            await BondReceiver(token);

            FindNodeMsg findNodeMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            findNodeMsg = AddReceiverFarAddress(findNodeMsg);

            Node[] expectedNodes = Enumerable.Repeat(new Node(TestItem.PublicKeyD, "192.168.1.3", 30303), 20).ToArray();
            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Any<PublicKey>(),
                Arg.Any<Node>())
                .Returns(expectedNodes);

            await _adapter.OnIncomingMsg(findNodeMsg);

            _kademliaMessageReceiver.GetKNeighbour(
                Arg.Is<PublicKey>(pk => pk.Bytes!.SequenceEqual(_testPublicKey.Bytes!)),
                Arg.Is<Node>(n => n.Id == _receiver.Id));

            // Send out two messages instead of one because of MTU limit.
            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.Nodes.Count == 12));
            await _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.Nodes.Count == 8));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_find_node_from_unbonded_peer_should_not_update_node_health(CancellationToken token)
        {
            FindNodeMsg findNodeMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            findNodeMsg = AddReceiverFarAddress(findNodeMsg);

            await _adapter.OnIncomingMsg(findNodeMsg);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id == _receiver.Id));
            _kademliaMessageReceiver.DidNotReceive().GetKNeighbour(Arg.Any<PublicKey>(), Arg.Any<Node>());
            await _msgSender.DidNotReceive().SendMsg(Arg.Any<NeighborsMsg>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_find_node_from_different_endpoint_should_not_respond(CancellationToken token)
        {
            await BondReceiver(token);

            IPEndPoint differentEndpoint = new(IPAddress.Parse("192.168.1.3"), _receiver.Address.Port);
            FindNodeMsg findNodeMsg = new(differentEndpoint, _timestamper.UnixTime.SecondsLong + 20, _testPublicKey.Bytes);
            findNodeMsg = AddReceiverFarAddress(findNodeMsg);

            await _adapter.OnIncomingMsg(findNodeMsg);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id == _receiver.Id));
            _kademliaMessageReceiver.DidNotReceive().GetKNeighbour(Arg.Any<PublicKey>(), Arg.Any<Node>());
            await _msgSender.DidNotReceive().SendMsg(Arg.Any<NeighborsMsg>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_enr_request_should_respond_with_enr_response(CancellationToken token)
        {
            await BondReceiver(token);

            EnrRequestMsg enrRequestMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);
            Hash256 expectedRequestHash = new(enrRequestMsg.Hash!.Value);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            await _msgSender.Received(1).SendMsg(Arg.Is<EnrResponseMsg>(m =>
                m.FarAddress!.Equals(_receiver.Address) &&
                m.RequestKeccak.Equals(expectedRequestHash) &&
                m.NodeRecord.Equals(_selfNodeRecord)));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_enr_request_after_inbound_ping_without_endpoint_bond_should_not_respond(CancellationToken token)
        {
            _adapter.GetSession(_receiver).OnPingReceived(_receiver.Address);

            EnrRequestMsg enrRequestMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrResponseMsg>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_enr_request_after_inbound_ping_from_different_endpoint_should_not_respond(CancellationToken token)
        {
            ConfigureBondCallback();

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address);
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);
            _msgSender.ClearReceivedCalls();

            IPEndPoint differentEndpoint = new(IPAddress.Parse("192.168.1.3"), _receiver.Address.Port);
            EnrRequestMsg enrRequestMsg = new(differentEndpoint, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrResponseMsg>());
        }

        [Test]
        [CancelAfter(10000)]
        public async Task OnIncomingMsg_enr_request_from_unbonded_peer_should_not_update_node_health(CancellationToken token)
        {
            EnrRequestMsg enrRequestMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id == _receiver.Id));
            await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrResponseMsg>());
        }

        private static bool HasNodeRecord(Node node, Node expectedNode, NodeRecord expectedRecord) =>
            node.Id.Equals(expectedNode.Id) &&
            node.Enr is not null &&
            node.Enr.ToString() == expectedRecord.ToString();
    }
}
