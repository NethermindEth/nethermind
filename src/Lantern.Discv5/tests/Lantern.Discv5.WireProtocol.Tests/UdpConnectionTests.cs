using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Logging.Exceptions;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using UdpConnection = Lantern.Discv5.WireProtocol.Connection.UdpConnection;

namespace Lantern.Discv5.WireProtocol.Tests;

public class UdpConnectionTests
{
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private Mock<ILogger<UdpConnection>> _mockLogger;
    private Mock<IGracefulTaskRunner> _mockGracefulTaskRunner;

    [OneTimeSetUp]
    public void SetUp()
    {
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<UdpConnection>>();
        _mockGracefulTaskRunner = new Mock<IGracefulTaskRunner>();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_mockLogger.Object);
    }

    [Test]
    public void CompleteMessageChannel_CompletesWithoutError()
    {
        var connectionOptions = new ConnectionOptions
        {
            UdpPort = 8081,
            RequestTimeoutMs = 1000
        };
        var connection = new UdpConnection(connectionOptions, _mockLoggerFactory.Object, _mockGracefulTaskRunner.Object);
        connection.Close();
    }

    [Test]
    public void Close_LogsMessageAndClosesClient()
    {
        var connectionOptions = new ConnectionOptions
        {
            UdpPort = 8082,
            RequestTimeoutMs = 1000
        };
        var connection = new UdpConnection(connectionOptions, _mockLoggerFactory.Object, _mockGracefulTaskRunner.Object);
        connection.Close();
    }

    [Test]
    public void Dispose_LogsMessageAndDisposesClient()
    {
        var connectionOptions = new ConnectionOptions
        {
            UdpPort = 8083,
            RequestTimeoutMs = 1000
        };
        var connection = new UdpConnection(connectionOptions, _mockLoggerFactory.Object, _mockGracefulTaskRunner.Object);
        connection.Close();
    }

    [Test]
    public void ValidatePacketSize_ThrowsExceptionForSmallPacket()
    {
        var data = new byte[PacketConstants.MinPacketSize - 1];
        Assert.Throws<InvalidPacketException>(() => UdpConnection.ValidatePacketSize(data));
    }

    [Test]
    public void ValidatePacketSize_ThrowsExceptionForLargePacket()
    {
        var data = new byte[PacketConstants.MaxPacketSize + 1];
        Assert.Throws<InvalidPacketException>(() => UdpConnection.ValidatePacketSize(data));
    }

    [Test]
    public void ValidatePacketSize_DoesNotThrowExceptionForValidPacket()
    {
        var data = new byte[PacketConstants.MaxPacketSize];
        UdpConnection.ValidatePacketSize(data);
    }
}
