using System.Net;
using System.Net.Sockets;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Packet.Handlers;
using Lantern.Discv5.WireProtocol.Packet.Headers;
using Lantern.Discv5.WireProtocol.Packet.Types;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class HandshakePacketHandlerTests
{
    private Mock<IIdentityManager> mockIdentityManager;
    private Mock<ISessionManager> mockSessionManager;
    private Mock<IRoutingTable> mockRoutingTable;
    private Mock<IMessageResponder> mockMessageResponder;
    private Mock<IUdpConnection> mockUdpConnection;
    private Mock<IPacketBuilder> mockPacketBuilder;
    private Mock<ISessionMain> mockSessionMain;
    private Mock<IEnrFactory> mockEnrRecordFactory;
    private Mock<ILoggerFactory> mockLoggerFactory;
    private Mock<ILogger<HandshakePacketHandler>> logger;
    private Mock<IPacketProcessor> mockPacketProcessor;

    [SetUp]
    public void Init()
    {
        mockIdentityManager = new Mock<IIdentityManager>();
        mockSessionManager = new Mock<ISessionManager>();
        mockRoutingTable = new Mock<IRoutingTable>();
        mockMessageResponder = new Mock<IMessageResponder>();
        mockUdpConnection = new Mock<IUdpConnection>();
        mockPacketBuilder = new Mock<IPacketBuilder>();
        mockSessionMain = new Mock<ISessionMain>();
        mockEnrRecordFactory = new Mock<IEnrFactory>();
        logger = new Mock<ILogger<HandshakePacketHandler>>();
        mockPacketProcessor = new Mock<IPacketProcessor>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public void Test_PacketHandlerType_ShouldReturn_HandshakeType()
    {
        Assert.AreEqual(PacketType.Handshake, new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object).PacketType);
    }

    [Test]
    public async Task Test_HandlePacket_ShouldReturn_WhenPublicKeyIsUnknown()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var handShakePacket = Convert.FromHexString(
            "0BB9627CD32F4E2F874A9308AEDB89D9944D8AF593E15B87953AEA4F3AA3F48B11A897160B15B76129A73C75F3FF401AACFCC85D764F2BED3EBD2763D4052608D17F7411AB731E4AB57FEF079CBE190A9FF257D94920F2982D3FCFC1E797AC619266CAD659D070B3D2D616FAC95881B00903D2FB999C3A3FCE67E8328E4B1DBC5901D5F9704914A92B2A5D065B29BBAE8B9FFE078A160040C61E766A0DB18CF3B57778621808D16C6F82BF1B61BB7D0BAA1CFA1D776F1DA9134ECF9C139770FBD358F0E860B5CF");
        var staticHeader = new StaticHeader(Convert.FromHexString("0001"), Convert.FromHexString("28422CCF35DCE7D21589520AF95E0C9AA0693CCD62E06E094E14C244EA3B735D40218E8CA024CD8B4EE54D247C5925CEF987DDF21E2868E4B9923EE7BAB9550D94330007BF291598DE6E790F6E857DF92422A8F24BBC08037861E0BDEBAB6E2199A8025E2C25291FA773566C3BC8E1C8025138393926452FA8666271579B399E32C91B"),
            2, Convert.FromHexString("0000000149A934A922AA1308"), 29);

        mockPacketProcessor
            .Setup(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny))
            .Returns((byte[] _, out StaticHeader? sh) =>
            {
                sh = staticHeader;
                return true;
            });

        // Arrange
        var handler = new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse("18.223.219.100"), 9000));

        // Act
        await handler.HandlePacket(fakeResult);

        // Assert
        mockPacketProcessor.Verify(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny), Times.Once);
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePacket_ShouldReturn_WhenSessionIsNull()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(Convert.FromHexString("0001"), Convert.FromHexString("28422CCF35DCE7D21589520AF95E0C9AA0693CCD62E06E094E14C244EA3B735D40218E8CA024CD8B4EE54D247C5925CEF987DDF21E2868E4B9923EE7BAB9550D94330007BF291598DE6E790F6E857DF92422A8F24BBC08037861E0BDEBAB6E2199A8025E2C25291FA773566C3BC8E1C8025138393926452FA8666271579B399E32C91B"),
            2, Convert.FromHexString("0000000149A934A922AA1308"), 29);

        mockPacketProcessor
            .Setup(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny))
            .Returns((byte[] _, out StaticHeader? sh) =>
            {
                sh = staticHeader;
                return true;
            });
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentityVerifierV4()));
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns((ISessionMain?)null);

        mockSessionMain
            .Setup(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(false);


        // Arrange
        var handler = new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse("18.223.219.100"), 9000));

        // Act
        await handler.HandlePacket(fakeResult);

        // Assert
        mockPacketProcessor.Verify(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny), Times.Once);
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePacket_ShouldReturn_WhenIdSignatureVerificationFails()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(Convert.FromHexString("0001"), Convert.FromHexString("28422CCF35DCE7D21589520AF95E0C9AA0693CCD62E06E094E14C244EA3B735D40218E8CA024CD8B4EE54D247C5925CEF987DDF21E2868E4B9923EE7BAB9550D94330007BF291598DE6E790F6E857DF92422A8F24BBC08037861E0BDEBAB6E2199A8025E2C25291FA773566C3BC8E1C8025138393926452FA8666271579B399E32C91B"),
            2, Convert.FromHexString("0000000149A934A922AA1308"), 29);

        mockPacketProcessor
            .Setup(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny))
            .Returns((byte[] _, out StaticHeader? sh) =>
            {
                sh = staticHeader;
                return true;
            });
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentityVerifierV4()));
        mockIdentityManager
            .SetupGet(x => x.Record)
            .Returns(enrRecord);
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockSessionMain
            .Setup(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(false);

        // Arrange
        var handler = new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse("18.223.219.100"), 9000));

        // Act
        await handler.HandlePacket(fakeResult);

        // Assert
        mockPacketProcessor.Verify(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny), Times.Once);
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionMain.Verify(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePacket_ShouldReturn_WhenMessageDecryptionFails()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(Convert.FromHexString("0001"), Convert.FromHexString("28422CCF35DCE7D21589520AF95E0C9AA0693CCD62E06E094E14C244EA3B735D40218E8CA024CD8B4EE54D247C5925CEF987DDF21E2868E4B9923EE7BAB9550D94330007BF291598DE6E790F6E857DF92422A8F24BBC08037861E0BDEBAB6E2199A8025E2C25291FA773566C3BC8E1C8025138393926452FA8666271579B399E32C91B"),
            2, Convert.FromHexString("0000000149A934A922AA1308"), 29);

        mockPacketProcessor
            .Setup(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny))
            .Returns((byte[] _, out StaticHeader? sh) =>
            {
                sh = staticHeader;
                return true;
            });
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentityVerifierV4()));
        mockIdentityManager
            .SetupGet(x => x.Record)
            .Returns(enrRecord);
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockSessionMain
            .Setup(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        mockSessionMain
            .Setup(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()))
            .Returns((byte[]?)null);

        // Arrange
        var handler = new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse("18.223.219.100"), 9000));

        // Act
        await handler.HandlePacket(fakeResult);

        // Assert
        mockPacketProcessor.Verify(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny), Times.Exactly(1));
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionMain.Verify(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()), Times.Once);
        mockMessageResponder.Verify(x => x.HandleMessageAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Never);
    }

    [Test]
    public async Task Test_HandlePacket_ShouldReturn_WhenThereIsNoReplyCreated()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(Convert.FromHexString("0001"), Convert.FromHexString("28422CCF35DCE7D21589520AF95E0C9AA0693CCD62E06E094E14C244EA3B735D40218E8CA024CD8B4EE54D247C5925CEF987DDF21E2868E4B9923EE7BAB9550D94330007BF291598DE6E790F6E857DF92422A8F24BBC08037861E0BDEBAB6E2199A8025E2C25291FA773566C3BC8E1C8025138393926452FA8666271579B399E32C91B"),
            2, Convert.FromHexString("0000000149A934A922AA1308"), 29);
        var packetTuple = new PacketResult(new byte[32], staticHeader);

        mockPacketProcessor
            .Setup(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny))
            .Returns((byte[] _, out StaticHeader? sh) =>
            {
                sh = staticHeader;
                return true;
            });
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentityVerifierV4()));
        mockIdentityManager
            .SetupGet(x => x.Record)
            .Returns(enrRecord);
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockSessionMain
            .Setup(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        mockMessageResponder
            .Setup(x => x.HandleMessageAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(Task.FromResult<byte[][]?>(null));
        mockSessionMain
            .Setup(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()))
            .Returns(new byte[32]);
        mockPacketBuilder
            .Setup(x => x.BuildOrdinaryPacket(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(packetTuple);

        // Arrange
        var handler = new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse("18.223.219.100"), 9000));

        // Act
        await handler.HandlePacket(fakeResult);

        // Assert
        mockPacketProcessor.Verify(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny), Times.Exactly(1));
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionMain.Verify(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()), Times.Once);
        mockMessageResponder.Verify(x => x.HandleMessageAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.EncryptMessage(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Never);
        mockUdpConnection.Verify(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Never);
    }

    [Test]
    [TestCase("18.223.219.100", 9000)]
    public async Task Test_HandlePacket_ShouldSendPacket_WhenReplyIsNotNull(string ip, int port)
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var staticHeader = new StaticHeader(Convert.FromHexString("0001"), Convert.FromHexString("28422CCF35DCE7D21589520AF95E0C9AA0693CCD62E06E094E14C244EA3B735D40218E8CA024CD8B4EE54D247C5925CEF987DDF21E2868E4B9923EE7BAB9550D94330007BF291598DE6E790F6E857DF92422A8F24BBC08037861E0BDEBAB6E2199A8025E2C25291FA773566C3BC8E1C8025138393926452FA8666271579B399E32C91B"),
            2, Convert.FromHexString("0000000149A934A922AA1308"), 29);
        var data = new List<byte[]> { new byte[32] }.ToArray();

        mockPacketProcessor
            .Setup(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny))
            .Returns((byte[] _, out StaticHeader? sh) =>
            {
                sh = staticHeader;
                return true;
            });
        mockRoutingTable
            .Setup(x => x.GetNodeEntryForNodeId(It.IsAny<byte[]>()))
            .Returns(new NodeTableEntry(enrRecord, new IdentityVerifierV4()));
        mockIdentityManager
            .SetupGet(x => x.Record)
            .Returns(enrRecord);
        mockSessionManager
            .Setup(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(mockSessionMain.Object);
        mockSessionMain
            .Setup(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()))
            .Returns(true);
        mockSessionMain
            .Setup(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()))
            .Returns(new byte[32]);
        mockMessageResponder
            .Setup(x => x.HandleMessageAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()))
            .Returns(Task.FromResult<byte[][]?>(data));
        mockPacketBuilder
            .Setup(x => x.BuildOrdinaryPacket(It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(),
                It.IsAny<byte[]>()))
            .Returns(new PacketResult(new byte[32], staticHeader));

        // Arrange
        var handler = new HandshakePacketHandler(mockIdentityManager.Object, mockSessionManager.Object, mockRoutingTable.Object,
            mockMessageResponder.Object, mockUdpConnection.Object, mockPacketBuilder.Object, mockPacketProcessor.Object, mockEnrRecordFactory.Object, mockLoggerFactory.Object);
        var fakeResult = new UdpReceiveResult(new byte[32], new IPEndPoint(IPAddress.Parse(ip), port));

        // Act
        await handler.HandlePacket(fakeResult);

        // Assert
        mockPacketProcessor.Verify(x => x.TryGetStaticHeader(It.IsAny<byte[]>(), out It.Ref<StaticHeader?>.IsAny), Times.Exactly(1));
        mockSessionManager.Verify(x => x.GetSession(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.VerifyIdSignature(It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockSessionMain.Verify(x => x.DecryptMessageWithNewKeys(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>(), It.IsAny<HandshakePacketBase>(), It.IsAny<byte[]>()), Times.Once);
        mockMessageResponder.Verify(x => x.HandleMessageAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
        mockSessionMain.Verify(x => x.EncryptMessage(It.IsAny<StaticHeader>(), It.IsAny<byte[]>(), It.IsAny<byte[]>()), Times.Once);
        mockUdpConnection.Verify(x => x.SendAsync(It.IsAny<byte[]>(), It.IsAny<IPEndPoint>()), Times.Once);
    }
}
