// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Portal;

public class KademliaContentNetworkContentKademliaMessageSenderTests
{
    private ITalkReqTransport _talkReqTransport = null!;
    private IEnrProvider _enrProvider = null!;
    private ContentNetworkProtocol _protocol = null!;

    private IEnr _testReceiverEnr = null!;

    [SetUp]
    public void Setup()
    {
        _talkReqTransport = Substitute.For<ITalkReqTransport>();
        IEnrFactory enrFactory = new EnrFactory(new EnrEntryRegistry());
        _enrProvider = Substitute.For<IEnrProvider>();
        _enrProvider.Decode(Arg.Any<byte[]>())
            .Returns((callInfo) => enrFactory.CreateFromBytes((byte[])callInfo[0], new IdentityVerifierV4()));

        _protocol = new ContentNetworkProtocol(
            new ContentNetworkConfig(),
            _talkReqTransport,
            LimboLogs.Instance);

        _testReceiverEnr = TestUtils.CreateEnr(TestItem.PrivateKeyA);
    }

    [Test]
    public async Task OnPing_ShouldSendTalkReq()
    {
        byte[] resultBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Pong,
            Pong = new Pong() { }
        });

        _talkReqTransport
            .CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Any<byte[]>(), default)
            .Returns(Task.FromResult(resultBytes));

        await _protocol.Ping(_testReceiverEnr, new Ping(), default);
        await _talkReqTransport.Received().CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Any<byte[]>(), default);
    }

    [Test]
    public async Task OnFindValue_ShouldSendTalkReqAndParseResponseCorrectly()
    {
        byte[] contentKey = [1, 2, 3, 4, 5];
        byte[] resultBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Content,
            Content = new Content()
            {
                Selector = ContentType.ConnectionId,
                ConnectionId = 0x1234
            }
        });

        _talkReqTransport
            .CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Any<byte[]>(), default)
            .Returns(Task.FromResult(resultBytes));

        Content result = await _protocol.FindContent(_testReceiverEnr, new FindContent()
        {
            ContentKey = new ContentKey()
            {
                Data = contentKey
            }
        }, default);

        result.ConnectionId.Should().Be(0x1234);
    }

    [Test]
    public async Task OnFindNeighbours_ShouldSendTalkReqAndParseResponseCorrectly()
    {
        var target = new ValueHash256(TestUtils.CreateEnr(TestItem.PrivateKeys[0]).NodeId);
        ushort dist = (ushort)Hash256XorUtils.CalculateDistance(new ValueHash256(_testReceiverEnr.NodeId), target);
        ushort[] queryDistances = [dist, (ushort)(dist + 1), (ushort)(dist - 1), (ushort)(dist + 2), (ushort)(dist - 2)];

        byte[] queryBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.FindNodes,
            FindNodes = new FindNodes()
            {
                Distances = queryDistances
            }
        });

        Discovery.Portal.Messages.Enr[] resultEnrs = new IEnr[] {
            TestUtils.CreateEnr(TestItem.PrivateKeyA),
            TestUtils.CreateEnr(TestItem.PrivateKeyB),
            TestUtils.CreateEnr(TestItem.PrivateKeyC),
            TestUtils.CreateEnr(TestItem.PrivateKeyD),
            TestUtils.CreateEnr(TestItem.PrivateKeyE),
        }.Select((enr) => new Discovery.Portal.Messages.Enr() { Data = enr.EncodeRecord() }).ToArray();
        byte[] resultBytes = SszEncoding.Encode(new MessageUnion()
        {
            Selector = MessageType.Nodes,
            Nodes = new Nodes()
            {
                Enrs = resultEnrs
            }
        });

        _talkReqTransport
            .CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Is<byte[]>(b => Bytes.AreEqual(queryBytes, b)), default)
            .Returns(Task.FromResult(resultBytes));

        Nodes result = await _protocol.FindNodes(_testReceiverEnr, new FindNodes()
        {
            Distances = queryDistances
        }, default);
        result.Enrs.Should().BeEquivalentTo(resultEnrs);
    }
}
