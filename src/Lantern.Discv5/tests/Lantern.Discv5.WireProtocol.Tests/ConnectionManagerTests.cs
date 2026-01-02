using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class ConnectionManagerTests
{
    private Mock<IPacketManager> _packetManagerMock;
    private Mock<IUdpConnection> _udpConnectionMock;
    private Mock<ILogger<ConnectionManager>> _loggerMock;
    private Mock<ICancellationTokenSourceWrapper> _cancellationTokenSourceMock;
    private Mock<IGracefulTaskRunner> _gracefulTaskRunnerMock;
    private Mock<ILoggerFactory> _loggerFactoryMock;
    private ConnectionManager _connectionManager;
    private CancellationTokenSource _source;

    [SetUp]
    public void SetUp()
    {
        _packetManagerMock = new Mock<IPacketManager>();
        _udpConnectionMock = new Mock<IUdpConnection>();
        _loggerMock = new Mock<ILogger<ConnectionManager>>();
        _gracefulTaskRunnerMock = new Mock<IGracefulTaskRunner>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_loggerMock.Object);
        _cancellationTokenSourceMock = new Mock<ICancellationTokenSourceWrapper>();
        _connectionManager = new ConnectionManager(_packetManagerMock.Object, _udpConnectionMock.Object, _cancellationTokenSourceMock.Object, _gracefulTaskRunnerMock.Object, _loggerFactoryMock.Object);
    }

    [Test]
    public void StartConnectionManagerAsync_AssertFunctionsCalled()
    {
        _connectionManager.InitAsync();
        _gracefulTaskRunnerMock.Verify(x => x.RunWithGracefulCancellationAsync(_udpConnectionMock.Object.ListenAsync, "Listen", _cancellationTokenSourceMock.Object.GetToken()), Times.Once);
        _gracefulTaskRunnerMock.Verify(x => x.RunWithGracefulCancellationAsync(_connectionManager.HandleIncomingPacketsAsync, "HandleIncomingPackets", _cancellationTokenSourceMock.Object.GetToken()), Times.Once);
    }

    [Test]
    public async Task StopConnectionManagerAsync_AssertsFunctionsCalled()
    {
        _connectionManager.InitAsync();
        await _connectionManager.StopConnectionManagerAsync();

        _udpConnectionMock.Verify(x => x.Close(), Times.Once);
        _cancellationTokenSourceMock.Verify(x => x.Cancel(), Times.Once);
    }
}
