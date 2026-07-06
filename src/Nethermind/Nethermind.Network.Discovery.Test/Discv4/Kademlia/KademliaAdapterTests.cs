// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
        private CancellationTokenSource _shutdownCts = null!;

        private void ConfigureBondCallback() =>
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
                        sent.Mdc!.Value);
                    pong.FarAddress = sent.FarAddress;
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

            _shutdownCts = new CancellationTokenSource();
            IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
            processExitSource.Token.Returns(_ => _shutdownCts.Token);

            _adapter = CreateAdapter(FailsafeRequestTimeoutMs, processExitSource);
        }

        private KademliaAdapter CreateAdapter(int requestTimeoutMs, IProcessExitSource? processExitSource = null) => new(
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
            processExitSource ?? Substitute.For<IProcessExitSource>(),
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
        public async Task TearDown()
        {
            await _adapter.DisposeAsync();
            _shutdownCts.Dispose();
        }

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

        private DiscoveryMsg CreateUnsolicitedResponse(MsgType msgType) =>
            msgType switch
            {
                MsgType.Pong => AddReceiverFarAddress(new PongMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, TestItem.KeccakA.ValueHash256)),
                MsgType.Neighbors => AddReceiverFarAddress(new NeighborsMsg(_receiver.Address, _timestamper.UnixTime.SecondsLong + 1, Array.Empty<Node>())),
                MsgType.EnrResponse => AddReceiverFarAddress(new EnrResponseMsg(_receiver.Address, _selfNodeRecord, TestItem.KeccakA)),
                _ => throw new ArgumentOutOfRangeException(nameof(msgType), msgType, null)
            };

        private NodeRecord ConfigureRemoteEnrRefresh(ulong advertisedSequence, ulong responseSequence)
        {
            NodeRecord remoteRecord = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyB,
                IPAddress.Parse("192.168.1.2"),
                tcpPort: null,
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
                    n.Id.Equals(_receiver.Id) &&
                    n.Enr != null &&
                    n.Enr.ToString() == remoteRecord.ToString()));
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
        public async Task OnIncomingMsg_ping_should_complete_gracefully_when_shutdown_during_enr_refresh(CancellationToken token)
        {
            const ulong advertisedSequence = 2;
            ConfigureRemoteEnrRefresh(advertisedSequence, advertisedSequence);

            _msgSender
                .When(x => x.SendMsg(Arg.Any<EnrRequestMsg>()))
                .Do(_ => _shutdownCts.Cancel());

            PingMsg pingMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20, _kademliaConfig.CurrentNodeId.Address)
            {
                EnrSequence = advertisedSequence
            };
            pingMsg.FarAddress = _receiver.Address;
            pingMsg = AddReceiverFarAddress(pingMsg);

            await _adapter.OnIncomingMsg(pingMsg);

            _nodeHealthTracker.DidNotReceive().OnRequestFailed(Arg.Is<Node>(n => n.Id.Equals(_receiver.Id)));
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
        public async Task OnIncomingMsg_enr_request_from_unbonded_peer_should_not_update_node_health(CancellationToken token)
        {
            EnrRequestMsg enrRequestMsg = new(_receiver.Address, _timestamper.UnixTime.SecondsLong + 20);
            enrRequestMsg = AddReceiverFarAddress(enrRequestMsg);

            await _adapter.OnIncomingMsg(enrRequestMsg);

            _nodeHealthTracker.DidNotReceive().OnIncomingMessageFrom(Arg.Is<Node>(n => n.Id == _receiver.Id));
            await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrResponseMsg>());
        }
    }
}
