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
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class MessageTests
{
    private static IEnrFactory _enrFactory = null!;
    private static IIdentityManager _identityManager = null!;
    private static IMessageDecoder _messageDecoder = null!;

    [SetUp]
    public void Setup()
    {
        var connectionOptions = new ConnectionOptions { UdpPort = 2030 };
        var sessionOptions = SessionOptions.Default;
        var loggerFactory = LoggingOptions.Default;
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(sessionOptions.Signer.PublicKey))
            .Build();

        _enrFactory = new EnrFactory(enrEntryRegistry);
        _identityManager = new IdentityManager(sessionOptions, connectionOptions, enr, loggerFactory);
        _messageDecoder = new MessageDecoder(_identityManager, _enrFactory);
    }

    [Test]
    public void Test_PingMessage_ShouldEncodeCorrectly()
    {
        var pingMessage = new PingMessage(12);
        var encodedMessage = pingMessage.EncodeMessage();
        var expectedPrefix = new[] { (byte)MessageType.Ping, 202, 136 };
        Assert.AreEqual(12, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(12, encodedMessage[^1]);
    }

    [Test]
    public void Test_PingMessage_ShouldDecodeCorrectly()
    {
        var pingMessage = new PingMessage(12);
        var newPingMessage = (PingMessage)_messageDecoder.DecodeMessage(pingMessage.EncodeMessage());
        Assert.AreEqual(pingMessage.RequestId, newPingMessage.RequestId);
        Assert.AreEqual(pingMessage.EnrSeq, newPingMessage.EnrSeq);
    }

    [Test]
    public void Test_PongMessage_ShouldEncodeCorrectly()
    {
        var recipientIp = IPAddress.Loopback;
        var pongMessage = new PongMessage(12, recipientIp, 3402);
        var encodedMessage = pongMessage.EncodeMessage();
        var expectedPrefix = new[] { 2, 210, 136 };
        var expectedSuffix = new[] { 12, 132, 127, 0, 0, 1, 130, 13, 74 };
        Assert.AreEqual(20, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 9));
    }

    [Test]
    public void Test_PongMessage_ShouldDecodeCorrectly()
    {
        var recipientIp = IPAddress.Loopback;
        var pongMessage = new PongMessage(12, recipientIp, 3402);
        var newPongMessage = (PongMessage)_messageDecoder.DecodeMessage(pongMessage.EncodeMessage());
        Assert.AreEqual(pongMessage.RequestId, newPongMessage.RequestId);
        Assert.AreEqual(pongMessage.EnrSeq, newPongMessage.EnrSeq);
        Assert.AreEqual(pongMessage.RecipientIp, newPongMessage.RecipientIp);
        Assert.AreEqual(pongMessage.RecipientPort, newPongMessage.RecipientPort);
    }

    [Test]
    public void Test_FindNode_ShouldEncodeCorrectly()
    {
        var firstNodeId = Convert.FromHexString("44b50f5e91964b67f544cec6d884bc27a83ae084fb2d0dae85c552722adde24c");
        var secondNodeId = Convert.FromHexString("922259344d3e88c6c34c94192f598dca417174209f9dbfd423038a6460c59bd6");
        var thirdNodeId = Convert.FromHexString("bd9261edff7e5908db711d9acd5470296af8a695646b3585255d8dc51a319e3c");
        var fourthNodeId = Convert.FromHexString("c4606371d7a8f19ff21404f7cb61c9d9f0a1440597717d6b0e5de92004f52ed9");
        var firstDistance = TableUtility.Log2Distance(firstNodeId, secondNodeId);
        var secondDistance = TableUtility.Log2Distance(thirdNodeId, fourthNodeId);
        var distances = new[] { firstDistance, secondDistance };
        var findNodeMessage = new FindNodeMessage(distances);
        var encodedMessage = findNodeMessage.EncodeMessage();
        var expectedPrefix = new[] { 3, 207, 136 };
        var expectedSuffix = new[] { 197, 130, 1, 0, 129, 255 };
        Assert.AreEqual(17, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 6));
    }

    [Test]
    public void Test_FindNode_ShouldDecodeCorrectly()
    {
        var firstNodeId = Convert.FromHexString("44b50f5e91964b67f544cec6d884bc27a83ae084fb2d0dae85c552722adde24c");
        var secondNodeId = Convert.FromHexString("922259344d3e88c6c34c94192f598dca417174209f9dbfd423038a6460c59bd6");
        var thirdNodeId = Convert.FromHexString("bd9261edff7e5908db711d9acd5470296af8a695646b3585255d8dc51a319e3c");
        var fourthNodeId = Convert.FromHexString("c4606371d7a8f19ff21404f7cb61c9d9f0a1440597717d6b0e5de92004f52ed9");
        var firstDistance = TableUtility.Log2Distance(firstNodeId, secondNodeId);
        var secondDistance = TableUtility.Log2Distance(thirdNodeId, fourthNodeId);
        var distances = new[] { firstDistance, secondDistance };
        var findNodeMessage = new FindNodeMessage(distances);
        var newFindNodeMessage = (FindNodeMessage)_messageDecoder.DecodeMessage(findNodeMessage.EncodeMessage());
        Assert.AreEqual(findNodeMessage.Distances, newFindNodeMessage.Distances);
    }

    [Test]
    public void Test_Nodes_ShouldEncodeCorrectly()
    {
        var firstEnrString =
            "enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";
        var secondEnrString =
            "enr:-Ly4QOS00hvPDddEcCpwA1cMykWNdJUK50AjbRgbLZ9FLPyBa78i0NwsQZLSV67elpJU71L1Pt9yqVmE1C6XeSI-LV8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpDuKNezAAAAckYFAAAAAAAAgmlkgnY0gmlwhEDhTgGJc2VjcDI1NmsxoQIgMUMFvJGlr8dI1TEQy-K78u2TJE2rWvah9nGqLQCEGohzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA";
        var enrEntryRegistry1 = new EnrEntryRegistry();
        var enrEntryRegistry2 = new EnrEntryRegistry();
        var enrs = new[]
        {
            new EnrFactory(enrEntryRegistry1).CreateFromString(firstEnrString, _identityManager.Verifier),
            new EnrFactory(enrEntryRegistry2).CreateFromString(secondEnrString, _identityManager.Verifier),
        };
        var nodesMessage = new NodesMessage(2, enrs);
        var encodedMessage = nodesMessage.EncodeMessage();
        var expectedPrefix = new[] { 4, 249, 1, 81, 136 };
        var expectedSuffix = new[]
        {
            249, 1, 68, 248, 132, 184, 64, 112, 152, 173, 134, 91, 0, 165, 130, 5, 25, 64, 203, 156, 243, 104, 54, 87,
            36, 17, 164, 114, 120, 120, 48, 119, 1, 21, 153, 237, 92, 209, 107, 118, 242, 99, 95, 78, 35, 71, 56, 243,
            8, 19, 168, 158, 185, 19, 126, 62, 61, 245, 38, 110, 58, 31, 17, 223, 114, 236, 241, 20, 92, 203, 156, 1,
            130, 105, 100, 130, 118, 52, 130, 105, 112, 132, 127, 0, 0, 1, 137, 115, 101, 99, 112, 50, 53, 54, 107, 49,
            161, 3, 202, 99, 76, 174, 13, 73, 172, 180, 1, 216, 164, 198, 182, 254, 140, 85, 183, 13, 17, 91, 244, 0,
            118, 156, 193, 64, 15, 50, 88, 205, 49, 56, 131, 117, 100, 112, 130, 118, 95, 248, 188, 184, 64, 228, 180,
            210, 27, 207, 13, 215, 68, 112, 42, 112, 3, 87, 12, 202, 69, 141, 116, 149, 10, 231, 64, 35, 109, 24, 27,
            45, 159, 69, 44, 252, 129, 107, 191, 34, 208, 220, 44, 65, 146, 210, 87, 174, 222, 150, 146, 84, 239, 82,
            245, 62, 223, 114, 169, 89, 132, 212, 46, 151, 121, 34, 62, 45, 95, 1, 135, 97, 116, 116, 110, 101, 116,
            115, 136, 0, 0, 0, 0, 0, 0, 0, 0, 132, 101, 116, 104, 50, 144, 238, 40, 215, 179, 0, 0, 0, 114, 70, 5, 0, 0,
            0, 0, 0, 0, 130, 105, 100, 130, 118, 52, 130, 105, 112, 132, 64, 225, 78, 1, 137, 115, 101, 99, 112, 50, 53,
            54, 107, 49, 161, 2, 32, 49, 67, 5, 188, 145, 165, 175, 199, 72, 213, 49, 16, 203, 226, 187, 242, 237, 147,
            36, 77, 171, 90, 246, 161, 246, 113, 170, 45, 0, 132, 26, 136, 115, 121, 110, 99, 110, 101, 116, 115, 0,
            131, 116, 99, 112, 130, 35, 40, 131, 117, 100, 112, 130, 35, 40
        };
        Assert.AreEqual(341, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 5));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 14, 327));
    }

    [Test]
    public void Test_Nodes_ShouldDecodeCorrectly()
    {
        var firstEnrString =
            "enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8";
        var secondEnrString =
            "enr:-Ly4QOS00hvPDddEcCpwA1cMykWNdJUK50AjbRgbLZ9FLPyBa78i0NwsQZLSV67elpJU71L1Pt9yqVmE1C6XeSI-LV8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpDuKNezAAAAckYFAAAAAAAAgmlkgnY0gmlwhEDhTgGJc2VjcDI1NmsxoQIgMUMFvJGlr8dI1TEQy-K78u2TJE2rWvah9nGqLQCEGohzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA";
        var enrEntryRegistry1 = new EnrEntryRegistry();
        var enrEntryRegistry2 = new EnrEntryRegistry();
        var enrs = new[]
        {
            new EnrFactory(enrEntryRegistry1).CreateFromString(firstEnrString, _identityManager.Verifier),
            new EnrFactory(enrEntryRegistry2).CreateFromString(secondEnrString, _identityManager.Verifier),
        };
        var nodesMessage = new NodesMessage(2, enrs);
        var decodedMessage = (NodesMessage)_messageDecoder.DecodeMessage(nodesMessage.EncodeMessage());

        Assert.AreEqual(decodedMessage.RequestId, nodesMessage.RequestId);
        Assert.AreEqual(decodedMessage.Total, nodesMessage.Total);
        Assert.AreEqual(decodedMessage.Enrs.Length, nodesMessage.Enrs.Length);

        for (var i = 0; i < decodedMessage.Enrs.Length; i++)
            Assert.AreEqual(decodedMessage.Enrs[i].ToString(), nodesMessage.Enrs[i].ToString());
    }

    [Test]
    public void Test_TalkReq_ShouldEncodeCorrectly()
    {
        var protocol = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var request = new byte[] { 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        var talkReqMessage = new TalkReqMessage(protocol, request);
        var encodedMessage = talkReqMessage.EncodeMessage();
        var expectedPrefix = new byte[] { 5, 223, 136 };
        var expectedSuffix = new byte[]
            { 138, 34, 12, 56, 41, 94, 24, 11, 67, 89, 30, 138, 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        Assert.AreEqual(33, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 22));
    }

    [Test]
    public void Test_TalkReq_ShouldDecodeCorrectly()
    {
        var protocol = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var request = new byte[] { 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        var talkReqMessage = new TalkReqMessage(protocol, request);
        var decodedMessage = (TalkReqMessage)_messageDecoder.DecodeMessage(talkReqMessage.EncodeMessage());
        Assert.AreEqual(decodedMessage.Protocol, talkReqMessage.Protocol);
        Assert.AreEqual(decodedMessage.Request, talkReqMessage.Request);
    }

    [Test]
    public void Test_TalkResp_ShouldEncodeCorrectly()
    {
        var response = new byte[] { 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        var talkRespMessage = new TalkRespMessage(response);
        var encodedMessage = talkRespMessage.EncodeMessage();
        var expectedPrefix = new byte[] { 6, 212, 136 };
        var expectedSuffix = new byte[] { 138, 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        Assert.AreEqual(22, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 11));
    }

    [Test]
    public void Test_TalkResp_ShouldDecodeCorrectly()
    {
        var response = new byte[] { 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        var talkRespMessage = new TalkRespMessage(response);
        var decodedMessage = (TalkRespMessage)_messageDecoder.DecodeMessage(talkRespMessage.EncodeMessage());
        Assert.AreEqual(decodedMessage.Response, talkRespMessage.Response);
    }

    [Test]
    public void Test_RegTopic_ShouldEncodeCorrectly()
    {
        var enrString =
            "enr:-Ly4QOS00hvPDddEcCpwA1cMykWNdJUK50AjbRgbLZ9FLPyBa78i0NwsQZLSV67elpJU71L1Pt9yqVmE1C6XeSI-LV8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpDuKNezAAAAckYFAAAAAAAAgmlkgnY0gmlwhEDhTgGJc2VjcDI1NmsxoQIgMUMFvJGlr8dI1TEQy-K78u2TJE2rWvah9nGqLQCEGohzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA";
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(enrString, _identityManager.Verifier);
        var topic = new byte[] { 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        var ticket = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var regTopicMessage = new RegTopicMessage(topic, enr, ticket);
        var encodedMessage = regTopicMessage.EncodeMessage();
        var expectedPrefix = new byte[] { 7, 248, 221, 136 };
        var expectedSuffix = new byte[]
        {
            138, 12, 45, 76, 10, 32, 92, 74, 56, 89, 34, 248, 188, 184, 64, 228, 180, 210, 27, 207, 13, 215, 68, 112,
            42, 112, 3, 87, 12, 202, 69, 141, 116, 149, 10, 231, 64, 35, 109, 24, 27, 45, 159, 69, 44, 252, 129, 107,
            191, 34, 208, 220, 44, 65, 146, 210, 87, 174, 222, 150, 146, 84, 239, 82, 245, 62, 223, 114, 169, 89, 132,
            212, 46, 151, 121, 34, 62, 45, 95, 1, 135, 97, 116, 116, 110, 101, 116, 115, 136, 0, 0, 0, 0, 0, 0, 0, 0,
            132, 101, 116, 104, 50, 144, 238, 40, 215, 179, 0, 0, 0, 114, 70, 5, 0, 0, 0, 0, 0, 0, 130, 105, 100, 130,
            118, 52, 130, 105, 112, 132, 64, 225, 78, 1, 137, 115, 101, 99, 112, 50, 53, 54, 107, 49, 161, 2, 32, 49,
            67, 5, 188, 145, 165, 175, 199, 72, 213, 49, 16, 203, 226, 187, 242, 237, 147, 36, 77, 171, 90, 246, 161,
            246, 113, 170, 45, 0, 132, 26, 136, 115, 121, 110, 99, 110, 101, 116, 115, 0, 131, 116, 99, 112, 130, 35,
            40, 131, 117, 100, 112, 130, 35, 40, 138, 34, 12, 56, 41, 94, 24, 11, 67, 89, 30
        };
        Assert.AreEqual(224, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 4));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 12, 212));
    }

    [Test]
    public void Test_RegTopic_ShouldDecodeCorrectly()
    {
        var enrString =
            "enr:-Ly4QOS00hvPDddEcCpwA1cMykWNdJUK50AjbRgbLZ9FLPyBa78i0NwsQZLSV67elpJU71L1Pt9yqVmE1C6XeSI-LV8Bh2F0dG5ldHOIAAAAAAAAAACEZXRoMpDuKNezAAAAckYFAAAAAAAAgmlkgnY0gmlwhEDhTgGJc2VjcDI1NmsxoQIgMUMFvJGlr8dI1TEQy-K78u2TJE2rWvah9nGqLQCEGohzeW5jbmV0cwCDdGNwgiMog3VkcIIjKA";
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(enrString, _identityManager.Verifier);
        var topic = new byte[] { 12, 45, 76, 10, 32, 92, 74, 56, 89, 34 };
        var ticket = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var regTopicMessage = new RegTopicMessage(topic, enr, ticket);
        var encodedMessage = regTopicMessage.EncodeMessage();
        var decodedMessage = (RegTopicMessage)_messageDecoder.DecodeMessage(encodedMessage);
        Assert.AreEqual(decodedMessage.Topic, regTopicMessage.Topic);
        Assert.AreEqual(regTopicMessage.Enr.ToString(), decodedMessage.Enr.ToString());
        Assert.AreEqual(decodedMessage.Ticket, regTopicMessage.Ticket);
    }

    [Test]
    public void Test_TicketResp_ShouldEncodeCorrectly()
    {
        var ticket = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var waitTime = 100;
        var ticketRespMessage = new TicketMessage(ticket, waitTime);
        var encodedMessage = ticketRespMessage.EncodeMessage();
        var expectedPrefix = new byte[] { 8, 213, 136 };
        var expectedSuffix = new byte[] { 138, 34, 12, 56, 41, 94, 24, 11, 67, 89, 30, 100 };
        Console.WriteLine(string.Join(", ", encodedMessage));
        Assert.AreEqual(23, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 12));
    }

    [Test]
    public void Test_TicketResp_ShouldDecodeCorrectly()
    {
        var ticket = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var waitTime = 100;
        var ticketRespMessage = new TicketMessage(ticket, waitTime);
        var encodedMessage = ticketRespMessage.EncodeMessage();
        var decodedMessage = (TicketMessage)_messageDecoder.DecodeMessage(encodedMessage);
        Assert.AreEqual(decodedMessage.Ticket, ticketRespMessage.Ticket);
        Assert.AreEqual(decodedMessage.WaitTime, ticketRespMessage.WaitTime);
    }

    [Test]
    public void Test_RegConfirmation_ShouldEncodeCorrectly()
    {
        var topic = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var regConfirmationMessage = new RegConfirmationMessage(topic);
        var encodedMessage = regConfirmationMessage.EncodeMessage();
        var expectedPrefix = new byte[] { 9, 212, 136 };
        var expectedSuffix = new byte[] { 138, 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        Assert.AreEqual(22, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 11));
    }

    [Test]
    public void Test_RegConfirmation_ShouldDecodeCorrectly()
    {
        var topic = new byte[] { 34, 12, 56, 41, 94, 24, 11, 67, 89, 30 };
        var regConfirmationMessage = new RegConfirmationMessage(topic);
        var encodedMessage = regConfirmationMessage.EncodeMessage();
        var decodedMessage = (RegConfirmationMessage)_messageDecoder.DecodeMessage(encodedMessage);
        Assert.AreEqual(decodedMessage.Topic, regConfirmationMessage.Topic);
    }

    [Test]
    public void Test_TopicQuery_ShouldEncodeCorrectly()
    {
        var topicHash = new byte[]
        {
            34, 12, 56, 41, 94, 24, 11, 67, 89, 30, 94, 24, 11, 67, 89, 30, 94, 24, 11, 67, 89, 30, 11, 67, 89, 30, 11,
            67, 89, 30, 53, 200
        };
        var topicQueryMessage = new TopicQueryMessage(topicHash);
        var encodedMessage = topicQueryMessage.EncodeMessage();
        var expectedPrefix = new byte[] { 10, 234, 136 };
        var expectedSuffix = new byte[]
        {
            160, 34, 12, 56, 41, 94, 24, 11, 67, 89, 30, 94, 24, 11, 67, 89, 30, 94, 24, 11, 67, 89, 30, 11, 67, 89, 30,
            11, 67, 89, 30, 53, 200
        };
        Assert.AreEqual(44, encodedMessage.Length);
        Assert.AreEqual(expectedPrefix, new ArraySegment<byte>(encodedMessage, 0, 3));
        Assert.AreEqual(expectedSuffix, new ArraySegment<byte>(encodedMessage, 11, 33));
    }

    [Test]
    public void Test_TopicQuery_ShouldDecodeCorrectly()
    {
        var topicHash = new byte[]
        {
            34, 12, 56, 41, 94, 24, 11, 67, 89, 30, 94, 24, 11, 67, 89, 30, 94, 24, 11, 67, 89, 30, 11, 67, 89, 30, 11,
            67, 89, 30, 53, 200
        };
        var topicQueryMessage = new TopicQueryMessage(topicHash);
        var encodedMessage = topicQueryMessage.EncodeMessage();
        var decodedMessage = (TopicQueryMessage)_messageDecoder.DecodeMessage(encodedMessage);
        Assert.AreEqual(decodedMessage.Topic, topicQueryMessage.Topic);
    }
}
