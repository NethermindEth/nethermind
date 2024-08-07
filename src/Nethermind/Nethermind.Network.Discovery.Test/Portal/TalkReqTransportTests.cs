// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Session;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Portal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Portal;

public class TalkReqTransportTests
{
    [Test]
    public void TestTalkReq()
    {
        IRawTalkReqSender sender = Substitute.For<IRawTalkReqSender>();
        ITalkReqTransport transport = new TalkReqTransport(sender, LimboLogs.Instance);
        IEnr enr = CreateEnr(TestItem.PrivateKeyA);

        byte[] protocol = [1, 2];
        byte[] message = [1, 2, 3, 4];

        transport
            .SendTalkReq(enr, protocol, message, default);

        sender
            .Received()
            .SentTalkReq(enr, protocol, message, default);
    }

    [Test]
    public async Task Test_CallAndWaitForResponse_ReceivedResponse()
    {
        IRawTalkReqSender sender = Substitute.For<IRawTalkReqSender>();
        ITalkReqTransport transport = new TalkReqTransport(sender, LimboLogs.Instance);

        IEnr enr = CreateEnr(TestItem.PrivateKeyA);

        byte[] protocol = [1, 2];
        byte[] message = [3, 4];
        byte[] responseMessage = [5, 6];

        TalkReqMessage reqMessage = new TalkReqMessage(protocol, message);
        sender.SentTalkReq(enr, protocol, message, default).Returns(reqMessage);

        Task<byte[]> task = transport.CallAndWaitForResponse(enr, protocol, message, default);

        TalkRespMessage respMessage = new TalkRespMessage(protocol, responseMessage);
        respMessage.RequestId = reqMessage.RequestId;
        transport.OnTalkResp(enr, respMessage);

        byte[] responseBytes = await task;
        responseBytes.Should().BeEquivalentTo(responseMessage);
    }

    [Test]
    public void Test_CallAndWaitForResponse_Timeout()
    {
        IRawTalkReqSender sender = Substitute.For<IRawTalkReqSender>();
        ITalkReqTransport transport = new TalkReqTransport(sender, LimboLogs.Instance);
        IEnr enr = CreateEnr(TestItem.PrivateKeyA);

        byte[] protocol = [1, 2];
        byte[] message = [1, 2, 3, 4];

        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        Task task = transport
            .CallAndWaitForResponse(enr, protocol, message, cts.Token);

        task.Should().Throws<OperationCanceledException>();
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
