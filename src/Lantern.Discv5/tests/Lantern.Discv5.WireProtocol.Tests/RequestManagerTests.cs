using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Messages.Requests;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class RequestManagerTests
{
    private Mock<IRoutingTable> _mockRoutingTable;
    private Mock<ICancellationTokenSourceWrapper> _cancellationTokenSourceMock;
    private Mock<IGracefulTaskRunner> _mockGracefulTaskRunner;
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private Mock<ILogger<RequestManager>> _mockLogger;
    private TableOptions _testTableOptions;
    private ConnectionOptions _testConnectionOptions;
    private RequestManager _requestManager;

    [SetUp]
    public void SetUp()
    {
        _mockRoutingTable = new Mock<IRoutingTable>();
        _cancellationTokenSourceMock = new Mock<ICancellationTokenSourceWrapper>();
        _mockGracefulTaskRunner = new Mock<IGracefulTaskRunner>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<RequestManager>>();
        _testTableOptions = TableOptions.Default;
        _testConnectionOptions = new ConnectionOptions();
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_mockLogger.Object);

        _requestManager = new RequestManager(
            _mockRoutingTable.Object,
            _mockLoggerFactory.Object,
            _cancellationTokenSourceMock.Object,
            _mockGracefulTaskRunner.Object,
            _testTableOptions,
            _testConnectionOptions
        );
    }

    [Test]
    public void TestAddPendingRequest_NewRequest()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var pendingRequest = new PendingRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));

        var result = _requestManager.AddPendingRequest(requestId, pendingRequest);

        Assert.IsTrue(result);
        _mockRoutingTable.Verify(x => x.MarkNodeAsPending(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public void TestAddPendingRequest_ExistingRequest()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var pendingRequest = new PendingRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));

        _requestManager.AddPendingRequest(requestId, pendingRequest);

        var result = _requestManager.AddPendingRequest(requestId, pendingRequest);

        Assert.IsFalse(result);
        _mockRoutingTable.Verify(x => x.MarkNodeAsPending(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public async Task TestStopRequestManagerAsync()
    {
        _requestManager.InitAsync();
        await Task.Delay(100);
        await _requestManager.StopRequestManagerAsync();

        _cancellationTokenSourceMock.Verify(x => x.Cancel(), Times.Once);
    }

    [Test]
    public void TestAddCachedRequest_NewRequest()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var cachedRequest = new CachedRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));

        var result = _requestManager.AddCachedRequest(requestId, cachedRequest);

        Assert.IsTrue(result);
        _mockRoutingTable.Verify(x => x.MarkNodeAsPending(It.IsAny<byte[]>()), Times.Once);
    }

    [Test]
    public void TestAddCachedHandshakeInteraction()
    {
        var packetNonce = new byte[] { 1, 2, 3 };
        var destNodeId = RandomUtility.GenerateRandomData(32);

        _requestManager.AddCachedHandshakeInteraction(packetNonce, destNodeId);

        var result = _requestManager.GetCachedHandshakeInteraction(packetNonce);

        Assert.AreEqual(destNodeId, result);
    }

    [Test]
    public void TestContainsCachedRequest_Exists()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var cachedRequest = new CachedRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));

        _requestManager.AddCachedRequest(requestId, cachedRequest);

        Assert.IsTrue(_requestManager.ContainsCachedRequest(requestId));
    }

    [Test]
    public void TestContainsCachedRequest_NotExists()
    {
        var requestId = new byte[] { 4, 5, 6 };
        Assert.IsFalse(_requestManager.ContainsCachedRequest(requestId));
    }

    [Test]
    public void TestMarkRequestAsFulfilled_Exists()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var pendingRequest = new PendingRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));

        _requestManager.AddPendingRequest(requestId, pendingRequest);

        var fulfilledRequest = _requestManager.MarkRequestAsFulfilled(requestId);

        Assert.NotNull(fulfilledRequest);
        Assert.IsTrue(fulfilledRequest.IsFulfilled);
        Assert.AreEqual(1, fulfilledRequest.ResponsesCount);
    }

    [Test]
    public void TestMarkRequestAsFulfilled_NotExists()
    {
        var requestId = new byte[] { 4, 5, 6 };
        var fulfilledRequest = _requestManager.MarkRequestAsFulfilled(requestId);
        Assert.Null(fulfilledRequest);
    }

    [Test]
    public void TestMarkCachedRequestAsFulfilled_Exists()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var cachedRequest = new CachedRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));

        _requestManager.AddCachedRequest(requestId, cachedRequest);

        var fulfilledRequest = _requestManager.MarkCachedRequestAsFulfilled(requestId);

        Assert.NotNull(fulfilledRequest);
        Assert.IsFalse(_requestManager.ContainsCachedRequest(requestId));
    }

    [Test]
    public void TestMarkCachedRequestAsFulfilled_NotExists()
    {
        var requestId = new byte[] { 4, 5, 6 };
        var fulfilledRequest = _requestManager.MarkCachedRequestAsFulfilled(requestId);
        Assert.Null(fulfilledRequest);
    }

    [Test]
    public void TestGetPendingRequest_Exists()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var pendingRequest = new PendingRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));
        _requestManager.AddPendingRequest(requestId, pendingRequest);

        var result = _requestManager.GetPendingRequest(requestId);
        Assert.AreEqual(pendingRequest, result);
    }

    [Test]
    public void TestGetPendingRequest_NotExists()
    {
        var requestId = new byte[] { 4, 5, 6 };

        var result = _requestManager.GetPendingRequest(requestId);
        Assert.IsNull(result);
    }

    [Test]
    public void TestGetPendingRequestByNodeId_Exists()
    {
        var nodeId = RandomUtility.GenerateRandomData(32);
        var requestId = new byte[] { 1, 2, 3 };
        var pendingRequest = new PendingRequest(nodeId, new PingMessage(1));
        _requestManager.AddPendingRequest(requestId, pendingRequest);

        var result = _requestManager.GetPendingRequestByNodeId(nodeId);
        Assert.AreEqual(pendingRequest, result);
    }

    [Test]
    public void TestGetPendingRequestByNodeId_NotExists()
    {
        var nodeId = RandomUtility.GenerateRandomData(32);

        var result = _requestManager.GetPendingRequestByNodeId(nodeId);
        Assert.IsNull(result);
    }

    [Test]
    public void TestGetCachedRequest_Exists()
    {
        var requestId = new byte[] { 1, 2, 3 };
        var cachedRequest = new CachedRequest(RandomUtility.GenerateRandomData(32), new PingMessage(1));
        _requestManager.AddCachedRequest(requestId, cachedRequest);

        var result = _requestManager.GetCachedRequest(requestId);
        Assert.AreEqual(cachedRequest, result);
    }

    [Test]
    public void TestGetCachedRequest_NotExists()
    {
        var requestId = new byte[] { 4, 5, 6 };

        var result = _requestManager.GetCachedRequest(requestId);
        Assert.IsNull(result);
    }
}
