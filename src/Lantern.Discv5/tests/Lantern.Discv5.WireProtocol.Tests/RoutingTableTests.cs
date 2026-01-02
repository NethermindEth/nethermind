using System.Net;
using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Logging;
using Lantern.Discv5.WireProtocol.Session;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

public class RoutingTableTests
{
    private IdentityManager _identityManager = null!;
    private Mock<IEnrFactory> mockEnrFactory = null!;
    private Mock<ILoggerFactory> mockLoggerFactory;
    private Mock<ILogger<RoutingTable>> logger;

    [SetUp]
    public void SetUp()
    {
        var connectionOptions = new ConnectionOptions { UdpPort = 2030 };
        var sessionOptions = SessionOptions.Default;
        var enr = new EnrBuilder()
            .WithIdentityScheme(sessionOptions.Verifier, sessionOptions.Signer)
            .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
            .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(sessionOptions.Signer.PublicKey))
            .Build();
        _identityManager = new IdentityManager(sessionOptions, connectionOptions, enr, LoggingOptions.Default);
        mockEnrFactory = new Mock<IEnrFactory>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        logger = new Mock<ILogger<RoutingTable>>();
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public void Test_RoutingTable_GetTotalEntriesCountAfterAddingNewNode()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enr = GenerateRandomEnrs(1)[0];
        routingTable.UpdateFromEnr(enr);

        var totalEntriesCount = routingTable.GetNodesCount();
        Assert.AreEqual(1, totalEntriesCount);
    }

    [Test]
    public void Test_RoutingTable_GetTotalActiveNodesCountAfterMarkingNodeAsLive()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enr = GenerateRandomEnrs(1)[0];
        routingTable.UpdateFromEnr(enr);
        var nodeId = _identityManager.Verifier.GetNodeIdFromRecord(enr);
        routingTable.MarkNodeAsLive(nodeId);

        var totalActiveNodesCount = routingTable.GetActiveNodesCount();
        Assert.AreEqual(1, totalActiveNodesCount);
    }

    [Test]
    public void Test_RoutingTable_GetAllNodeEntriesAfterAddingNewNode()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enr = GenerateRandomEnrs(1)[0];
        routingTable.UpdateFromEnr(enr);

        var allNodeEntries = routingTable.GetAllNodes().ToArray();
        Assert.AreEqual(1, allNodeEntries.Length);
    }

    [Test]
    public void Test_RoutingTable_GetNodeEntry()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enr = GenerateRandomEnrs(1)[0];
        routingTable.UpdateFromEnr(enr);
        var nodeEntry = routingTable.GetNodeEntryForNodeId(_identityManager.Verifier.GetNodeIdFromRecord(enr));

        Assert.IsNotNull(nodeEntry);
        Assert.AreEqual(_identityManager.Verifier.GetNodeIdFromRecord(enr), nodeEntry.Id);
    }

    [Test]
    public void Test_RoutingTable_IncreaseFailureCounter()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enr = GenerateRandomEnrs(1)[0];
        routingTable.UpdateFromEnr(enr);
        var nodeId = _identityManager.Verifier.GetNodeIdFromRecord(enr);
        routingTable.IncreaseFailureCounter(nodeId);
        var nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);

        Assert.AreEqual(1, nodeEntry.FailureCounter);
    }

    [Test]
    public void Test_RoutingTable_GetClosestNodes()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enrs = GenerateRandomEnrs(3).ToList();

        foreach (var enr in enrs)
        {
            routingTable.UpdateFromEnr(enr);
            routingTable.MarkNodeAsLive(_identityManager.Verifier.GetNodeIdFromRecord(enr));
        }

        var closestNodes = routingTable.GetClosestNodes(enrs[0].NodeId);
        Assert.AreEqual(1, closestNodes.Count(node => node.Record.NodeId.SequenceEqual(enrs[0].NodeId)));
    }

    [Test]
    public void Test_RoutingTable_LeastRecentlySeenNode()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enrs1 = GenerateRandomEnrs(3).ToList();

        foreach (var enr in enrs1)
        {
            routingTable.UpdateFromEnr(enr);
            routingTable.MarkNodeAsLive(_identityManager.Verifier.GetNodeIdFromRecord(enr));
        }

        Thread.Sleep(1000);
        var randomEnr = GenerateRandomEnrs(1)[0];

        routingTable.UpdateFromEnr(randomEnr);
        routingTable.MarkNodeAsLive(_identityManager.Verifier.GetNodeIdFromRecord(randomEnr));

        var leastRecentlySeen = routingTable.GetLeastRecentlySeenNode();
        Assert.AreNotEqual(_identityManager.Verifier.GetNodeIdFromRecord(randomEnr), leastRecentlySeen.Id);
    }

    [Test]
    public void Test_RoutingTable_NodeStatusChange()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var enr = GenerateRandomEnrs(1)[0];
        routingTable.UpdateFromEnr(enr);
        var nodeId = _identityManager.Verifier.GetNodeIdFromRecord(enr);

        routingTable.MarkNodeAsLive(nodeId);
        var nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);
        Assert.AreEqual(NodeStatus.Live, nodeEntry.Status);

        routingTable.MarkNodeAsDead(nodeId);
        nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);
        Assert.AreEqual(NodeStatus.Dead, nodeEntry.Status);

        routingTable.MarkNodeAsPending(nodeId);
        nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);
        Assert.AreEqual(NodeStatus.Pending, nodeEntry.Status);

        routingTable.MarkNodeAsResponded(nodeId);
        nodeEntry = routingTable.GetNodeEntryForNodeId(nodeId);
        Assert.IsTrue(nodeEntry.HasRespondedEver);
    }

    [Test]
    public void Test_RoutingTable_GenerateErrorWhenGivenInvalidDistance()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);

        var invalidDistance = -10;

        var enrs = GenerateRandomEnrs(3);

        foreach (var enr in enrs)
        {
            routingTable.UpdateFromEnr(enr);
            routingTable.MarkNodeAsLive(_identityManager.Verifier.GetNodeIdFromRecord(enr));
        }

        // Capture Log
        var logs = new List<string>();
        logger.Setup(x => x.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception, string>>()))
            .Callback(new InvocationAction(invocation =>
            {
                logs.Add(invocation.Arguments[2].ToString());
            }));

        routingTable.GetEnrRecordsAtDistances(new[] { invalidDistance });

        Assert.IsTrue(logs.Any(log => log.Contains("Distance should be between 0 and 256")));
    }

    [Test]
    public void Test_RoutingTable_MarkNonExistentNodeAsLive()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);

        var nonExistentNode = new byte[] { 0, 1, 2, 3, 4, 5 };
        routingTable.MarkNodeAsLive(nonExistentNode);

        var totalActiveNodesCount = routingTable.GetActiveNodesCount();
        Assert.AreEqual(0, totalActiveNodesCount);
    }

    [Test]
    public void Test_RoutingTable_IncreaseFailureCounterOfNonExistentNode()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);

        var nonExistentNode = new byte[] { 0, 1, 2, 3, 4, 5 };

        routingTable.IncreaseFailureCounter(nonExistentNode);

        var nodeEntry = routingTable.GetNodeEntryForNodeId(nonExistentNode);
        Assert.IsNull(nodeEntry);
    }

    [Test]
    public void Test_RoutingTable_MarkNonExistentNodeAsDead()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var nonExistentNode = new byte[] { 0, 1, 2, 3, 4, 5 };

        routingTable.MarkNodeAsDead(nonExistentNode);

        var nodeEntry = routingTable.GetNodeEntryForNodeId(nonExistentNode);
        Assert.IsNull(nodeEntry);
    }

    [Test]
    public void Test_RoutingTable_MarkNonExistentNodeAsPending()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var nonExistentNode = new byte[] { 0, 1, 2, 3, 4, 5 };

        routingTable.MarkNodeAsPending(nonExistentNode);

        var nodeEntry = routingTable.GetNodeEntryForNodeId(nonExistentNode);
        Assert.IsNull(nodeEntry);
    }

    [Test]
    public void Test_RoutingTable_MarkNonExistentNodeAsResponded()
    {
        var routingTable = new RoutingTable(_identityManager, mockEnrFactory.Object, mockLoggerFactory.Object,
            TableOptions.Default);
        var nonExistentNode = new byte[] { 0, 1, 2, 3, 4, 5 };

        routingTable.MarkNodeAsResponded(nonExistentNode);

        var nodeEntry = routingTable.GetNodeEntryForNodeId(nonExistentNode);
        Assert.IsNull(nodeEntry);
    }

    private Enr.Enr[] GenerateRandomEnrs(int count)
    {
        var enrs = new Enr.Enr[count];

        for (var i = 0; i < count; i++)
        {
            var signer = new IdentitySignerV4(RandomUtility.GenerateRandomData(32));
            var ipAddress = new IPAddress(RandomUtility.GenerateRandomData(4));

            enrs[i] = new EnrBuilder()
                .WithIdentityScheme(_identityManager.Verifier, _identityManager.Signer)
                .WithEntry(EnrEntryKey.Id, new EntryId("v4"))
                .WithEntry(EnrEntryKey.Ip, new EntryIp(ipAddress))
                .WithEntry(EnrEntryKey.Udp, new EntryUdp(Random.Shared.Next(0, 9000)))
                .WithEntry(EnrEntryKey.Secp256K1, new EntrySecp256K1(signer.PublicKey))
                .Build();
        }

        return enrs;
    }
}
