// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.Portal;
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
        IEnr enr = TestUtils.CreateEnr(TestItem.PrivateKeyA);

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

        IEnr enr = TestUtils.CreateEnr(TestItem.PrivateKeyA);

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
        IEnr enr = TestUtils.CreateEnr(TestItem.PrivateKeyA);

        byte[] protocol = [1, 2];
        byte[] message = [1, 2, 3, 4];

        using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        Task task = transport
            .CallAndWaitForResponse(enr, protocol, message, cts.Token);

        task.Should().Throws<OperationCanceledException>();
    }
}
