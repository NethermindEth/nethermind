using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Logging;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class MessageRequesterTests
{
    private Mock<IIdentityManager> mockIdentityManager = null!;
    private Mock<IRequestManager> mockRequestManager = null!;
    private Mock<ILoggerFactory> mockLoggerFactory;
    private Mock<ILogger<MessageRequester>> logger;

    private IIdentityManager _identityManager = null!;
    private IEnrFactory _enrFactory = null!;
    private IMessageRequester _messageRequester = null!;
    private IDiscv5Protocol _discv5Protocol = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var sessionOptions = SessionOptions.Default;
        var connectionOptions = new ConnectionOptions { UdpPort = 2030 };
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
            .WithLoggerFactory(loggerFactory));

        var serviceProvider = builder.GetServiceProvider();
        _discv5Protocol = serviceProvider.GetRequiredService<IDiscv5Protocol>();
        _identityManager = serviceProvider.GetRequiredService<IIdentityManager>();
        _enrFactory = serviceProvider.GetRequiredService<IEnrFactory>();
        _messageRequester = serviceProvider.GetRequiredService<IMessageRequester>();

        mockIdentityManager = new Mock<IIdentityManager>();
        mockRequestManager = new Mock<IRequestManager>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        logger = new Mock<ILogger<MessageRequester>>();
        logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(), (Func<It.IsAnyType, Exception, string>)It.IsAny<object>()));
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public void Test_MessageRequester_ShouldGeneratePingMessageCorrectly()
    {
        var destNodeId = RandomUtility.GenerateRandomData(32);
        var pingMessage = _messageRequester.ConstructPingMessage(destNodeId)!;
        var cachedPingMessage = _messageRequester.ConstructCachedPingMessage(destNodeId)!;
        var decodedPingMessage = (PingMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(pingMessage);
        var decodedCachedPingMessage = (PingMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(cachedPingMessage);

        Assert.AreEqual(MessageType.Ping, decodedPingMessage.MessageType);
        Assert.AreEqual(_identityManager.Record.SequenceNumber, decodedPingMessage.EnrSeq);
        Assert.AreEqual(MessageType.Ping, decodedCachedPingMessage.MessageType);
        Assert.AreEqual(_identityManager.Record.SequenceNumber, decodedCachedPingMessage.EnrSeq);
    }

    [Test]
    public void Test_MessageRequester_ShouldGenerateFindNodeMessageCorrectly()
    {
        var destNodeId = RandomUtility.GenerateRandomData(32);
        var targetNodeId = RandomUtility.GenerateRandomData(32);

        int distance = TableUtility.Log2Distance(destNodeId, targetNodeId);
        var findNodeMessage = _messageRequester.ConstructFindNodeMessage(destNodeId, false, [distance, distance - 1])!;
        var cachedFindNodeMessage = _messageRequester.ConstructCachedFindNodeMessage(destNodeId, false, [distance])!;
        var decodedFindNodeMessage = (FindNodeMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(findNodeMessage);
        var decodedCachedFindNodeMessage = (FindNodeMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(cachedFindNodeMessage);

        Assert.AreEqual(MessageType.FindNode, decodedFindNodeMessage.MessageType);
        Assert.AreEqual(distance, decodedFindNodeMessage.Distances.First());
        Assert.AreEqual(MessageType.FindNode, decodedCachedFindNodeMessage.MessageType);
        Assert.AreEqual(distance, decodedCachedFindNodeMessage.Distances.First());
    }

    [Test]
    public void Test_MessageRequester_ShouldGenerateTalkRequestMessageCorrectly()
    {
        var destNodeId = RandomUtility.GenerateRandomData(32);
        var protocol = "discv5"u8.ToArray();
        var request = "ping"u8.ToArray();
        var talkRequestMessage = _messageRequester.ConstructTalkReqMessage(destNodeId, protocol, request)!;
        var cachedTalkRequestMessage = _messageRequester.ConstructCachedTalkReqMessage(destNodeId, protocol, request)!;
        var decodedTalkRequestMessage = (TalkReqMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(talkRequestMessage);
        var cachedDecodedTalkRequestMessage = (TalkReqMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(cachedTalkRequestMessage);

        Assert.AreEqual(MessageType.TalkReq, decodedTalkRequestMessage.MessageType);
        Assert.AreEqual(protocol, decodedTalkRequestMessage.Protocol);
        Assert.AreEqual(request, decodedTalkRequestMessage.Request);
        Assert.AreEqual(MessageType.TalkReq, cachedDecodedTalkRequestMessage.MessageType);
        Assert.AreEqual(protocol, cachedDecodedTalkRequestMessage.Protocol);
        Assert.AreEqual(request, cachedDecodedTalkRequestMessage.Request);
    }

    [Test]
    public void Test_MessageRequester_ShouldGenerateTalkResponseMessageCorrectly()
    {
        var destNodeId = RandomUtility.GenerateRandomData(32);
        var response = "response"u8.ToArray();
        var talkResponseMessage = _messageRequester.ConstructTalkRespMessage(destNodeId, response)!;
        var cachedTalkResponseMessage = _messageRequester.ConstructCachedTalkRespMessage(destNodeId, response)!;
        var decodedTalkResponseMessage = (TalkRespMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(talkResponseMessage);
        var cachedDecodedTalkResponseMessage = (TalkRespMessage)new MessageDecoder(_identityManager, _enrFactory).DecodeMessage(cachedTalkResponseMessage);

        Assert.AreEqual(MessageType.TalkResp, decodedTalkResponseMessage.MessageType);
        Assert.AreEqual(response, decodedTalkResponseMessage.Response);
        Assert.AreEqual(MessageType.TalkResp, cachedDecodedTalkResponseMessage.MessageType);
        Assert.AreEqual(response, cachedDecodedTalkResponseMessage.Response);
    }

    [Test]
    public void Test_MessageRequester_ShouldReturnWhenUnableToAddRequests()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());

        mockIdentityManager
            .Setup(x => x.Record)
            .Returns(enrRecord);
        mockRequestManager
            .Setup(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()))
            .Returns(false);
        mockRequestManager
            .Setup(x => x.AddCachedRequest(It.IsAny<byte[]>(), It.IsAny<CachedRequest>()))
            .Returns(false);

        var messageRequester = new MessageRequester(mockIdentityManager.Object, mockRequestManager.Object, mockLoggerFactory.Object);
        var destNodeId = RandomUtility.GenerateRandomData(32);
        var pingResult = messageRequester.ConstructPingMessage(destNodeId);
        var cachedPingResult = messageRequester.ConstructCachedPingMessage(destNodeId);
        var findNodeResult = messageRequester.ConstructFindNodeMessage(destNodeId, false, [0]);
        var cachedFindNodeResult = messageRequester.ConstructCachedFindNodeMessage(destNodeId, false, [0]);
        var talkRequestResult = messageRequester.ConstructTalkReqMessage(destNodeId, "discv5"u8.ToArray(), "ping"u8.ToArray());
        var cachedTalkRequestResult = messageRequester.ConstructCachedTalkReqMessage(destNodeId, "discv5"u8.ToArray(), "ping"u8.ToArray());
        var talkResponseResult = messageRequester.ConstructTalkRespMessage(destNodeId, "response"u8.ToArray());
        var cachedTalkResponseResult = messageRequester.ConstructCachedTalkRespMessage(destNodeId, "response"u8.ToArray());

        Assert.IsNull(pingResult);
        Assert.IsNull(cachedPingResult);
        Assert.IsNull(findNodeResult);
        Assert.IsNull(cachedFindNodeResult);
        Assert.IsNull(talkRequestResult);
        Assert.IsNull(cachedTalkRequestResult);
        Assert.IsNull(talkResponseResult);
        Assert.IsNull(cachedTalkResponseResult);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _discv5Protocol.StopAsync();
    }
}
