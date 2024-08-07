// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Session;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal;
using Nethermind.Network.Discovery.Portal.Messages;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Portal;

public class KademliaTalkReqMessageSenderTests
{
    private ITalkReqTransport _talkReqTransport = null!;
    private IEnrProvider _enrProvider = null!;
    private KademliaTalkReqMessageSender _messageSender = null!;

    private IEnr _testReceiverEnr = null!;

    [SetUp]
    public void Setup()
    {
        _talkReqTransport = Substitute.For<ITalkReqTransport>();
        IEnrFactory enrFactory = new EnrFactory(new EnrEntryRegistry());
        _enrProvider = Substitute.For<IEnrProvider>();
        _enrProvider.Decode(Arg.Any<byte[]>())
            .Returns((callInfo) => enrFactory.CreateFromBytes((byte[])callInfo[0], new IdentityVerifierV4()));

        _messageSender = new KademliaTalkReqMessageSender(
            new ContentNetworkConfig(),
            _talkReqTransport,
            _enrProvider,
            LimboLogs.Instance);

        _testReceiverEnr = CreateEnr(TestItem.PrivateKeyA);
    }

    [Test]
    public async Task OnPing_ShouldSendTalkReq()
    {
        await _messageSender.Ping(_testReceiverEnr, default);
        await _talkReqTransport.Received().CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Any<byte[]>(), default);
    }

    [Test]
    public async Task OnFindValue_ShouldSendTalkReqAndParseResponseCorrectly()
    {
        byte[] contentKey = [1, 2, 3, 4, 5];
        byte[] resultBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            Content = new Content()
            {
                ConnectionId = 0x1234
            }
        });

        _talkReqTransport
            .CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Any<byte[]>(), default)
            .Returns(Task.FromResult(resultBytes));

        FindValueResponse<IEnr, LookupContentResult> result = await _messageSender.FindValue(_testReceiverEnr, contentKey, default);
        result.value!.ConnectionId.Should().Be(0x1234);
    }

    [Test]
    public async Task OnFindNeighbours_ShouldSendTalkReqAndParseResponseCorrectly()
    {
        var target = new ValueHash256(CreateEnr(TestItem.PrivateKeys[0]).NodeId);
        ushort dist = (ushort)Hash256XORUtils.CalculateDistance(new ValueHash256(_testReceiverEnr.NodeId), target);
        ushort[] queryDistances = [dist, (ushort)(dist + 1), (ushort)(dist - 1), (ushort)(dist + 2), (ushort)(dist - 2)];

        byte[] queryBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            FindNodes = new FindNodes()
            {
                Distances = queryDistances
            }
        });

        byte[][] resultEnrs = new IEnr[] {
            CreateEnr(TestItem.PrivateKeyA),
            CreateEnr(TestItem.PrivateKeyB),
            CreateEnr(TestItem.PrivateKeyC),
            CreateEnr(TestItem.PrivateKeyD),
            CreateEnr(TestItem.PrivateKeyE),
        }.Select((enr) => enr.EncodeRecord()).ToArray();
        byte[] resultBytes = SlowSSZ.Serialize(new MessageUnion()
        {
            Nodes = new Nodes()
            {
                Enrs = resultEnrs
            }
        });

        _talkReqTransport
            .CallAndWaitForResponse(_testReceiverEnr, Arg.Any<byte[]>(), Arg.Is<byte[]>(b => Bytes.AreEqual(queryBytes, b)), default)
            .Returns(Task.FromResult(resultBytes));

        IEnr[] result = await _messageSender.FindNeighbours(_testReceiverEnr, target, default);
        result.Select((enr) => enr.EncodeRecord()).ToArray().Should().BeEquivalentTo(resultEnrs);
    }

    private IEnr CreateEnr(PrivateKey privateKey)
    {
        SessionOptions sessionOptions = new SessionOptions
        {
            Signer = new IdentitySignerV4(privateKey.KeyBytes),
            Verifier = new IdentityVerifierV4(),
            SessionKeys = new SessionKeys(privateKey.KeyBytes),
        };

        return new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(
                NBitcoin.Secp256k1.Context.Instance.CreatePubKey(privateKey.PublicKey.PrefixedBytes).ToBytes(false)
            ))
            .Build();
    }
}
