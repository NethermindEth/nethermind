using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

public class MockMessageResponderTests
{
    private Mock<IIdentityManager> mockIdentityManager = null!;
    private Mock<IRoutingTable> mockRoutingTable = null!;
    private Mock<IPacketReceiver> mockPacketReceiver = null!;
    private Mock<IRequestManager> mockRequestManager = null!;
    private Mock<ILookupManager> mockLookupManager = null!;
    private Mock<IMessageDecoder> mockMessageDecoder = null!;
    private Mock<ILoggerFactory> mockLoggerFactory = null!;
    private Mock<ITalkReqAndRespHandler> mockTalkReqAndRespHandler = null!;
    private Mock<ILogger<MessageResponder>> logger = null!;

    [SetUp]
    public void Setup()
    {
        mockIdentityManager = new Mock<IIdentityManager>();
        mockRoutingTable = new Mock<IRoutingTable>();
        mockRequestManager = new Mock<IRequestManager>();
        mockPacketReceiver = new Mock<IPacketReceiver>();
        mockLookupManager = new Mock<ILookupManager>();
        mockMessageDecoder = new Mock<IMessageDecoder>();
        mockTalkReqAndRespHandler = new Mock<ITalkReqAndRespHandler>();
        logger = new Mock<ILogger<MessageResponder>>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldReturnNull_WhenPendingRequestIsNotAvailable()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(1, sender.Address, sender.Port);

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns((PendingRequest?)null);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Never);
        mockRoutingTable.Verify(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldReturnNull_WhenNodeEntryIsNull()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(1, sender.Address, sender.Port);
        var pendingRequest = new PendingRequest(new byte[32], pongMessage);

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager.Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager
            .Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsResponded(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.UpdateFromEnr(It.IsAny<Enr.Enr>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldUpdateNode_WhenNodeStatusIsNotLive()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(1, sender.Address, sender.Port);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var pendingRequest = new PendingRequest(new byte[32], pongMessage);

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager.Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enr, new IdentityVerifierV4()));
        mockIdentityManager
            .Setup(x => x.IsIpAddressAndPortSet())
            .Returns(true);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Exactly(2));
        mockRoutingTable.Verify(x => x.MarkNodeAsResponded(It.IsAny<byte[]>()), Times.Exactly(2));
        mockRoutingTable.Verify(x => x.UpdateFromEnr(It.IsAny<Enr.Enr>()), Times.Once);
        mockIdentityManager.Verify(x => x.UpdateIpAddressAndPort(It.IsAny<IPEndPoint>()), Times.Never);
        mockRequestManager.Verify(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldUpdateIpAddressAndPort_WhenNotSet()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(1, sender.Address, sender.Port);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var pendingRequest = new PendingRequest(new byte[32], pongMessage);

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager.Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enr, new IdentityVerifierV4()));
        mockIdentityManager
            .Setup(x => x.IsIpAddressAndPortSet())
            .Returns(false);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Exactly(2));
        mockRoutingTable.Verify(x => x.MarkNodeAsResponded(It.IsAny<byte[]>()), Times.Exactly(2));
        mockRoutingTable.Verify(x => x.UpdateFromEnr(It.IsAny<Enr.Enr>()), Times.Once);
        mockIdentityManager.Verify(x => x.UpdateIpAddressAndPort(It.IsAny<IPEndPoint>()), Times.Once);
        mockRequestManager.Verify(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldReturnNull_WhenNodeHasTheLatestSeqValue()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(1, sender.Address, sender.Port);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var pendingRequest = new PendingRequest(new byte[32], pongMessage);
        var nodeEntry = new NodeTableEntry(enr, new IdentityVerifierV4());

        nodeEntry.Status = NodeStatus.Live;

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager.Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(nodeEntry);
        mockIdentityManager
            .Setup(x => x.IsIpAddressAndPortSet())
            .Returns(false);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Exactly(1));
        mockRoutingTable.Verify(x => x.MarkNodeAsResponded(It.IsAny<byte[]>()), Times.Once);
        mockRequestManager.Verify(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldReturnNull_WhenFailedToAddPendingRequest()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(4, sender.Address, sender.Port);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var pendingRequest = new PendingRequest(new byte[32], pongMessage);
        var nodeEntry = new NodeTableEntry(enr, new IdentityVerifierV4())
        {
            Status = NodeStatus.Live,
        };

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager.Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(nodeEntry);
        mockIdentityManager
            .Setup(x => x.IsIpAddressAndPortSet())
            .Returns(false);
        mockRequestManager
            .Setup(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()))
            .Returns(false);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        var result = await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Exactly(1));
        mockRoutingTable.Verify(x => x.MarkNodeAsResponded(It.IsAny<byte[]>()), Times.Once);
        mockRequestManager.Verify(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()), Times.Once);
        Assert.IsNull(result);
    }

    [Test]
    public async Task Test_HandlePongMessage_ShouldReturnEncodedMessage_WhenPendingRequestIsAdded()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var pongMessage = new PongMessage(4, sender.Address, sender.Port);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var pendingRequest = new PendingRequest(new byte[32], pongMessage);
        var nodeEntry = new NodeTableEntry(enr, new IdentityVerifierV4());

        nodeEntry.Status = NodeStatus.Live;

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(pongMessage);
        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager.Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(nodeEntry);
        mockIdentityManager
            .Setup(x => x.IsIpAddressAndPortSet())
            .Returns(false);
        mockRequestManager
            .Setup(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()))
            .Returns(true);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        var result = await messageResponder.HandleMessageAsync(pongMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockRoutingTable.Verify(x => x.MarkNodeAsLive(It.IsAny<byte[]>()), Times.Exactly(1));
        mockRoutingTable.Verify(x => x.MarkNodeAsResponded(It.IsAny<byte[]>()), Times.Once);
        mockRequestManager.Verify(x => x.AddPendingRequest(It.IsAny<byte[]>(), It.IsAny<PendingRequest>()), Times.Once);
        Assert.IsNotNull(result);
    }

    [Test]
    public async Task Test_HandleNodesMessage_ShouldReturnNull_WhenPendingRequestIsNotAvailable()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var nodesMessage = new NodesMessage(1, new[] { enr });

        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns((PendingRequest?)null);
        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(nodesMessage);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(nodesMessage.EncodeMessage(), sender);

        mockRequestManager.Verify(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()), Times.Once);
        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public async Task Test_HandleNodesMessage_ShouldReturnNull_WhenReceivedMoreResponsesThanExpected()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var enrEntryRegistry = new EnrEntryRegistry();
        var enr = new EnrFactory(enrEntryRegistry).CreateFromString(
            "enr:-JK4QJfxa51DquJ5N32adFyvFJC_8R5edMNmmOm_4W2y5GZ0B_kK6Q-jCbS87xWt1HD63-0NP7L9QRDP34iosikpG6EDgmlkgnY0g2lwNpAgAQ24haMAAAAAii4DcHM0iXNlY3AyNTZrMaEDfBECt-cliWdTBpsKMDXTNdTOnUvuOv_JT85v7T2Os-6EdWRwNoIE0g", new IdentityVerifierV4());
        var nodesMessage = new NodesMessage(5, new[] { enr });
        var pendingRequest = new PendingRequest(new byte[32], nodesMessage);

        pendingRequest.ResponsesCount = 6;

        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager
            .Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(nodesMessage);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(nodesMessage.EncodeMessage(), sender);

        mockRequestManager.Verify(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()), Times.Once);
        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public async Task Test_HandleTalkReqMessage_ShouldReturnNull_WhenInterfaceIsNotSet()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var talkReqMessage = new TalkReqMessage(new byte[32], new byte[32]);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(talkReqMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task Test_HandleTalkReqMessage_ShouldReturnNull_WhenResultIsNull()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var talkReqMessage = new TalkReqMessage(new byte[32], new byte[32]);

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(talkReqMessage);
        mockTalkReqAndRespHandler
            .Setup(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[][]?)null);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object, mockTalkReqAndRespHandler.Object);
        var result = await messageResponder.HandleMessageAsync(talkReqMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockTalkReqAndRespHandler.Verify(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        Assert.IsNull(result);
    }

    [Test]
    public async Task Test_HandleTalkReqMessage_ShouldReturnMessage_WhenResultIsNotNull()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var talkReqMessage = new TalkReqMessage(new byte[32], new byte[32]);

        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(talkReqMessage);
        mockTalkReqAndRespHandler
            .Setup(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(new List<byte[]>().ToArray);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object, mockTalkReqAndRespHandler.Object);
        var result = await messageResponder.HandleMessageAsync(talkReqMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockTalkReqAndRespHandler.Verify(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        Assert.IsNotNull(result);
    }

    [Test]
    public async Task Test_HandleTalkRespMessage_ShouldReturnNull_WhenInterfaceIsNotSet()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var talkRespMessage = new TalkRespMessage(new byte[32], new byte[32]);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object);
        await messageResponder.HandleMessageAsync(talkRespMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task Test_HandleTalkRespMessage_ShouldReturnNull_WhenPendingRequestIsNull()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var talkRespMessage = new TalkRespMessage(new byte[32], new byte[32]);

        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns((PendingRequest?)null);
        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(talkRespMessage);
        mockTalkReqAndRespHandler
            .Setup(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[][]?)null);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object, mockTalkReqAndRespHandler.Object);
        var result = await messageResponder.HandleMessageAsync(talkRespMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockTalkReqAndRespHandler.Verify(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
        Assert.IsNull(result);
    }

    [Test]
    public async Task Test_HandleTalkRespMessage_ShouldHandleMessage_WhenPendingRequestIsNotNull()
    {
        var sender = new IPEndPoint(IPAddress.Any, 2020);
        var talkRespMessage = new TalkRespMessage(new byte[32], new byte[32]);
        var pendingRequest = new PendingRequest(new byte[32], talkRespMessage);

        mockRequestManager
            .Setup(x => x.MarkRequestAsFulfilled(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockRequestManager
            .Setup(x => x.GetPendingRequest(It.IsAny<byte[]>()))
            .Returns(pendingRequest);
        mockMessageDecoder
            .Setup(x => x.DecodeMessage(It.IsAny<byte[]>()))
            .Returns(talkRespMessage);
        mockTalkReqAndRespHandler
            .Setup(x => x.HandleRequest(It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns((byte[][]?)null);

        var messageResponder = new MessageResponder(mockIdentityManager.Object, mockRoutingTable.Object, mockPacketReceiver.Object, mockRequestManager.Object, mockLookupManager.Object, mockMessageDecoder.Object, mockLoggerFactory.Object, mockTalkReqAndRespHandler.Object);
        var result = await messageResponder.HandleMessageAsync(talkRespMessage.EncodeMessage(), sender);

        mockMessageDecoder.Verify(x => x.DecodeMessage(It.IsAny<byte[]>()), Times.Once);
        mockTalkReqAndRespHandler.Verify(x => x.HandleResponse(It.IsAny<byte[]>()), Times.Once);
        Assert.IsNull(result);
    }


}
