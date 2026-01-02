using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Responses;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

public class Discv5ProtocolMockTests
{
    private Discv5Protocol _discv5Protocol;
    private Mock<IConnectionManager> mockConnectionManager = null!;
    private Mock<ITableManager> mockTableManager = null!;
    private Mock<IRequestManager> mockRequestManager = null!;
    private Mock<IPacketReceiver> mockPacketReceiver = null!;
    private Mock<IPacketManager> mockPacketManager = null!;
    private Mock<IRoutingTable> mockRoutingTable = null!;
    private Mock<ISessionManager> mockSessionManager = null!;
    private Mock<ILookupManager> mockLookupManager = null!;
    private Mock<IIdentityManager> mockIdentityManager = null!;
    private Mock<ILogger<Discv5Protocol>> mockLogger = null!;
    private Mock<ILoggerFactory> mockLoggerFactory = null!;

    [SetUp]
    public void Init()
    {
        mockConnectionManager = new Mock<IConnectionManager>();
        mockTableManager = new Mock<ITableManager>();
        mockRequestManager = new Mock<IRequestManager>();
        mockPacketReceiver = new Mock<IPacketReceiver>();
        mockPacketManager = new Mock<IPacketManager>();
        mockRoutingTable = new Mock<IRoutingTable>();
        mockSessionManager = new Mock<ISessionManager>();
        mockLookupManager = new Mock<ILookupManager>();
        mockIdentityManager = new Mock<IIdentityManager>();
        mockLogger = new Mock<ILogger<Discv5Protocol>>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(mockLogger.Object);
    }



    [Test]
    public void StartProtocol_InvokesStartMethodsOnServices()
    {
        SetupServices();
        mockConnectionManager.Verify(cm => cm.InitAsync(), Times.Once);
        mockTableManager.Verify(tm => tm.InitAsync(), Times.Once);
        mockRequestManager.Verify(rm => rm.InitAsync(), Times.Once);
    }

    [Test]
    public async Task StopProtocolAsync_InvokesStopMethodsOnServices()
    {
        SetupServices();
        await _discv5Protocol.StopAsync();
        mockConnectionManager.Verify(cm => cm.StopConnectionManagerAsync(), Times.Once);
        mockTableManager.Verify(tm => tm.StopTableManagerAsync(), Times.Once);
        mockRequestManager.Verify(rm => rm.StopRequestManagerAsync(), Times.Once);
    }

    [Test]
    public void ShouldReturnSelfEnrRecord()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        mockIdentityManager
            .Setup(x => x.Record)
            .Returns(enrRecord);

        SetupServices();
        var result = _discv5Protocol.SelfEnr;
        Assert.AreEqual(result, enrRecord);
    }

    [Test]
    public void ShouldReturnNodesCount()
    {
        mockRoutingTable
            .Setup(x => x.GetNodesCount())
            .Returns(10);

        SetupServices();
        var result = _discv5Protocol.NodesCount;
        Assert.AreEqual(result, 10);
    }

    [Test]
    public void ShouldReturnPeerCount()
    {
        mockRoutingTable
            .Setup(x => x.GetActiveNodesCount())
            .Returns(10);

        SetupServices();
        var result = _discv5Protocol.PeerCount;
        Assert.AreEqual(result, 10);
    }

    [Test]
    public void ShouldReturnActiveSessionCount()
    {
        mockSessionManager
            .Setup(x => x.TotalSessionCount)
            .Returns(10);

        SetupServices();
        var result = _discv5Protocol.ActiveSessionCount;
        Assert.AreEqual(result, 10);
    }

    [Test]
    public void ShouldReturnNodeFromId()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var nodeEntry = new NodeTableEntry(enrRecord, new IdentityVerifierV4());
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(nodeEntry.Id))
            .Returns(nodeEntry);

        SetupServices();
        var result = _discv5Protocol.GetEnrForNodeId(nodeEntry.Id);
        Assert.IsTrue(result.NodeId.SequenceEqual(nodeEntry.Id));
    }

    [Test]
    public void ShouldReturnAllNodes()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());

        mockRoutingTable
            .Setup(x => x.GetAllNodes())
            .Returns(new[] { enrRecord });

        SetupServices();
        var result = _discv5Protocol.GetAllNodes.ToArray();
        Assert.IsTrue(result.Length == 1);
        Assert.IsTrue(result[0].NodeId.SequenceEqual(enrRecord.NodeId));
    }

    [Test]
    public async Task PerformLookupAsync_ShouldReturnNull_WhenNoActiveNodes()
    {
        mockRoutingTable
            .Setup(x => x.GetNodesCount())
            .Returns(0);

        SetupServices();
        var result = await _discv5Protocol.DiscoverAsync(RandomUtility.GenerateRandomData(32));
        Assert.IsNull(result);
    }

    [Test]
    public async Task SendPingAsync_ShouldReturnPongResponse_WhenNoExceptionIsThrown()
    {
        // Arrange
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());

        mockPacketReceiver
            .Setup(x => x.SendPingAsync(It.IsAny<Enr.Enr>()))
            .ReturnsAsync(new PongMessage(1, IPAddress.Parse("127.0.0.0"), 2));

        SetupServices();

        // Act
        var result = await _discv5Protocol.SendPingAsync(enrRecord);

        mockPacketReceiver.Verify(x => x.SendPingAsync(enrRecord), Times.Once);

        Assert.IsNotNull(result);
        Assert.AreEqual(result!.EnrSeq, 1);
        Assert.AreEqual(result!.RecipientIp, IPAddress.Parse("127.0.0.0"));
        Assert.AreEqual(result!.RecipientPort, 2);
    }

    [Test]
    public async Task SendPingAsync_ShouldReturnNull_WhenExceptionThrown()
    {
        // Arrange
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var exceptionToThrow = new Exception("Test exception");

        mockPacketReceiver
            .Setup(x => x.SendPingAsync(It.IsAny<Enr.Enr>()))
            .ThrowsAsync(exceptionToThrow);

        SetupServices();

        // Act
        var result = await _discv5Protocol.SendPingAsync(enrRecord);

        mockPacketReceiver.Verify(x => x.SendPingAsync(enrRecord), Times.Once);

        Assert.IsNull(result);
    }

    [Test]
    public async Task SendFindNodeAsync_ShouldReturnEnrResponse_WhenNoExceptionIsThrown()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());

        mockPacketReceiver
            .Setup(x => x.SendFindNodeAsync(It.IsAny<Enr.Enr>(), It.IsAny<byte[]>()))
            .Returns(Task.FromResult(new IEnr[] { enrRecord })!);

        SetupServices();

        var result = await _discv5Protocol.SendFindNodeAsync(enrRecord, RandomUtility.GenerateRandomData(32));

        mockPacketReceiver.Verify(x => x.SendFindNodeAsync(enrRecord, It.IsAny<byte[]>()), Times.Once);

        Assert.IsNotNull(result);
    }

    [TestCase([0])]
    [TestCase([256])]
    [TestCase([254, 255, 256])]
    public async Task SendFindNodeAsync_ShouldSendDistances(params int[] distances)
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());

        mockPacketReceiver
            .Setup(x => x.SendFindNodeAsync(It.IsAny<Enr.Enr>(), It.IsAny<int[]>()))
            .Returns(Task.FromResult(new IEnr[] { enrRecord })!);

        SetupServices();

        var result = await _discv5Protocol.SendFindNodeAsync(enrRecord, distances);

        mockPacketReceiver.Verify(x => x.SendFindNodeAsync(enrRecord, distances), Times.Once);
    }

    [Test]
    public async Task SendFindNodeAsync_ShouldReturnFalse_WhenExceptionIsThrown()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var exceptionToThrow = new Exception("Test exception");

        mockPacketReceiver
            .Setup(x => x.SendFindNodeAsync(It.IsAny<Enr.Enr>(), It.IsAny<byte[]>()))
            .ThrowsAsync(exceptionToThrow);

        SetupServices();

        var result = await _discv5Protocol.SendFindNodeAsync(enrRecord, RandomUtility.GenerateRandomData(32));

        mockPacketReceiver.Verify(x => x.SendFindNodeAsync(enrRecord, It.IsAny<byte[]>()), Times.Once);

        Assert.IsNull(result);
    }

    [Test]
    public async Task SendTalkReqAsync_ShouldReturnTrue_WhenNoExceptionIsThrown()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());

        mockPacketManager
            .Setup(x => x.SendPacket(It.IsAny<Enr.Enr>(), It.IsAny<MessageType>(), It.IsAny<bool>(), It.IsAny<object[]>()))
            .Returns(Task.FromResult(new byte[0]));

        SetupServices();
        var result = await _discv5Protocol.SendTalkReqAsync(enrRecord, RandomUtility.GenerateRandomData(32), RandomUtility.GenerateRandomData(32));
        mockPacketManager.Verify(x => x.SendPacket(enrRecord, MessageType.TalkReq, It.IsAny<bool>(), It.IsAny<object[]>()), Times.Once);
        Assert.IsTrue(result);
    }

    [Test]
    public async Task SendTalkReqAsync_ShouldReturnFalse_WhenExceptionIsThrown()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var exceptionToThrow = new Exception("Test exception");

        mockPacketManager
            .Setup(x => x.SendPacket(It.IsAny<Enr.Enr>(), It.IsAny<MessageType>(), It.IsAny<bool>(), It.IsAny<object[]>()))
            .ThrowsAsync(exceptionToThrow);

        SetupServices();

        var result = await _discv5Protocol.SendTalkReqAsync(enrRecord, RandomUtility.GenerateRandomData(32), RandomUtility.GenerateRandomData(32));
        mockPacketManager.Verify(x => x.SendPacket(enrRecord, MessageType.TalkReq, It.IsAny<bool>(), It.IsAny<object[]>()), Times.Once);
        Assert.IsFalse(result);
    }

    private void SetupServices()
    {
        _discv5Protocol = new Discv5Protocol(
            mockConnectionManager.Object,
            mockIdentityManager.Object,
            mockTableManager.Object,
            mockRequestManager.Object,
            mockPacketReceiver.Object,
            mockPacketManager.Object,
            mockRoutingTable.Object,
            mockSessionManager.Object,
            mockLookupManager.Object,
            mockLoggerFactory.Object.CreateLogger<Discv5Protocol>()
        );

        _discv5Protocol.InitAsync();
    }
}
