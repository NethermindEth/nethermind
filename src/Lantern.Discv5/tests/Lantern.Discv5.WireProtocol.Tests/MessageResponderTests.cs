using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Logging;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class MessageResponderTests
{
    private IMessageResponder _messageResponder = null!;
    private IEnrFactory _enrFactory = null!;
    private IIdentityManager _identityManager = null!;
    private IDiscv5Protocol _discv5Protocol = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var connectionOptions = new ConnectionOptions { UdpPort = 2030 };
        var sessionOptions = SessionOptions.Default;
        var tableOptions = TableOptions.Default;
        var loggerFactory = LoggingOptions.Default;
        var enrBuilder = new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(sessionOptions.Signer.PublicKey));

        var services = new ServiceCollection();
        var builder = services.AddDiscv5(builder => builder
            .WithConnectionOptions(connectionOptions)
            .WithSessionOptions(sessionOptions)
            .WithTableOptions(tableOptions)
            .WithEnrBuilder(enrBuilder)
            .WithLoggerFactory(loggerFactory)
            .WithTalkResponder(new TestTalkReqAndRespHandler()));

        var serviceProvider = builder.GetServiceProvider();
        _discv5Protocol = serviceProvider.GetRequiredService<IDiscv5Protocol>();
        _messageResponder = serviceProvider.GetRequiredService<IMessageResponder>();
        _identityManager = serviceProvider.GetRequiredService<IIdentityManager>();
        _enrFactory = serviceProvider.GetRequiredService<IEnrFactory>();
    }

    [Test]
    public async Task Test_MessageResponder_ShouldThrowArgumentException_WhenMessageIsNotSupported()
    {
        var topicMessage = new TopicQueryMessage("test"u8.ToArray());
        var result =
            await _messageResponder.HandleMessageAsync(topicMessage.EncodeMessage(), new IPEndPoint(IPAddress.Any, 9000));
        Assert.Null(result);
    }

    [Test]
    public async Task Test_MessageResponder_ShouldHandlePingMessageCorrectly()
    {
        var pingMessage = new PingMessage((int)_identityManager.Record.SequenceNumber);
        var ipEndPoint = new IPEndPoint(IPAddress.Any, 9989);
        var response = await _messageResponder.HandleMessageAsync(pingMessage.EncodeMessage(), ipEndPoint);
        var pongMessage = (PongMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(response[0]!);

        Assert.NotNull(pongMessage);
        Assert.AreEqual(MessageType.Pong, pongMessage.MessageType);
        Assert.AreEqual(_identityManager.Record.SequenceNumber, pongMessage.EnrSeq);
        Assert.AreEqual(ipEndPoint.Address, pongMessage.RecipientIp);
        Assert.AreEqual(ipEndPoint.Port, pongMessage.RecipientPort);
    }

    [Test]
    public async Task Test_MessageResponder_ShouldHandleFindNodesMessageCorrectly()
    {
        var distances = new[] { 252, 253, 254 };
        var findNodesMessage = new FindNodeMessage(distances);
        var ipEndPoint = new IPEndPoint(IPAddress.Any, 9319);
        var response = await _messageResponder.HandleMessageAsync(findNodesMessage.EncodeMessage(), ipEndPoint);
        var nodesMessage = (NodesMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(response[0]!);
        Assert.NotNull(nodesMessage);
        Assert.AreEqual(MessageType.Nodes, nodesMessage.MessageType);
        Assert.AreEqual(0, nodesMessage.Total);
        Assert.AreEqual(0, nodesMessage.Enrs.Length);
    }

    [Test]
    public async Task Test_MessageResponder_ShouldHandleTalkRequestMessageCorrectly()
    {
        var talkRequestMessage = new TalkReqMessage("protocol"u8.ToArray(), "request"u8.ToArray());
        var ipEndPoint = new IPEndPoint(IPAddress.Any, 9312);
        var response = await _messageResponder.HandleMessageAsync(talkRequestMessage.EncodeMessage(), ipEndPoint);
        var talkRespMessage = (TalkRespMessage)new MessageDecoder(_identityManager, _enrFactory)
            .DecodeMessage(response[0]!);
        Assert.NotNull(talkRespMessage);
        Assert.AreEqual(MessageType.TalkResp, talkRespMessage.MessageType);
        Assert.AreEqual("request"u8.ToArray(), talkRespMessage.Response);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _discv5Protocol.StopAsync();
    }

    private class TestTalkReqAndRespHandler : ITalkReqAndRespHandler
    {
        public byte[][] HandleRequest(byte[] protocol, byte[] request)
        {
            return new[] { request };
        }

        public byte[] HandleResponse(byte[] response)
        {
            return response;
        }
    }
}
