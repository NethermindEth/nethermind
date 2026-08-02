// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Network.Test;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Xdc.Discovery;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Discovery;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcKademliaAdapterTests
{
    private const int RequestTimeoutMs = 10_000;

    private IKademlia<PublicKey, Node> _kademliaMessageReceiver = null!;
    private INodeHealthTracker<Node> _nodeHealthTracker = null!;
    private KademliaConfig<Node> _kademliaConfig = null!;
    private ITimestamper _timestamper = null!;
    private IMsgSender _msgSender = null!;
    private INodeStatsManager _nodeStatsManager = null!;
    private INodeRecordProvider _nodeRecordProvider = null!;
    private IMessageSerializationService _receiverSerializationManager = null!;
    private Node _receiver = null!;
    private XdcKademliaAdapter _adapter = null!;

    [SetUp]
    public void Setup()
    {
        Node currentNode = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
        _kademliaMessageReceiver = Substitute.For<IKademlia<PublicKey, Node>>();
        _nodeHealthTracker = Substitute.For<INodeHealthTracker<Node>>();
        _kademliaConfig = new KademliaConfig<Node> { CurrentNodeId = currentNode };

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
        _nodeRecordProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<NodeRecord>(new NodeRecord()));
        _nodeStatsManager = Substitute.For<INodeStatsManager>();
        _nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns(Substitute.For<INodeStats>());

        _adapter = new XdcKademliaAdapter(
            new Lazy<IKademlia<PublicKey, Node>>(() => _kademliaMessageReceiver),
            new Lazy<INodeHealthTracker<Node>>(() => _nodeHealthTracker),
            new DiscoveryConfig
            {
                EnrTimeout = RequestTimeoutMs,
                PingTimeout = RequestTimeoutMs,
                SendNodeTimeout = RequestTimeoutMs,
                BondWaitTime = 1,
            },
            _kademliaConfig,
            _nodeRecordProvider,
            _nodeStatsManager,
            _timestamper,
            Substitute.For<IProcessExitSource>(),
            new Ecdsa(),
            LimboLogs.Instance)
        {
            MsgSender = _msgSender,
        };
    }

    [TearDown]
    public async Task TearDown() => await _adapter.DisposeAsync();

    private void ConfigureBondCallback(ulong? pongEnrSequence) =>
        _msgSender
            .SendMsg(Arg.Any<PingMsg>())
            .Returns(ci =>
            {
                PingMsg sent = (PingMsg)ci[0]!;
                using DisposableByteBuffer buffer = _receiverSerializationManager.ZeroSerialize(sent).AsDisposable();
                PingMsg msg = _receiverSerializationManager.Deserialize<PingMsg>(buffer);
                PongMsg pong = new(msg.FarPublicKey!, _timestamper.UnixTime.SecondsLong + 1, sent.Mdc!.Value, pongEnrSequence);
                pong.FarAddress = sent.FarAddress;
                return _adapter.OnIncomingMsg(pong);
            });

    [Test]
    [CancelAfter(10000)]
    public async Task Ping_should_bond_but_never_send_enr_request_even_when_pong_advertises_newer_sequence(CancellationToken token)
    {
        ConfigureBondCallback(pongEnrSequence: 42);

        bool result = await _adapter.Ping(_receiver, token);

        Assert.That(result, Is.True);
        await _msgSender.DidNotReceive().SendMsg(Arg.Any<EnrRequestMsg>());
        _kademliaMessageReceiver.DidNotReceive().AddOrRefresh(Arg.Is<Node>(n => n.Enr != null));
    }
}
