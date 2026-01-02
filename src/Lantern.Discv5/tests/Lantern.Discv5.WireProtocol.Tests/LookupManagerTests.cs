using Lantern.Discv5.Enr;
using Lantern.Discv5.Enr.Identity.V4;
using Lantern.Discv5.WireProtocol.Connection;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Table;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class LookupManagerTests
{
    private Mock<IRoutingTable> mockRoutingTable = null!;
    private Mock<IPacketManager> mockPacketManager = null!;
    private ConnectionOptions connectionOptions = null!;
    private TableOptions tableOptions = null!;
    private Mock<IRequestManager> mockRequestManager = null!;
    private Mock<ILoggerFactory> mockLoggerFactory;
    private Mock<ILogger<LookupManager>> logger;

    [SetUp]
    public void Init()
    {
        mockRoutingTable = new Mock<IRoutingTable>();
        mockPacketManager = new Mock<IPacketManager>();
        connectionOptions = new ConnectionOptions();
        tableOptions = TableOptions.Default;
        mockRequestManager = new Mock<IRequestManager>();
        mockLoggerFactory = new Mock<ILoggerFactory>();
        logger = new Mock<ILogger<LookupManager>>();
        mockLoggerFactory
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(logger.Object);
    }

    [Test]
    public async Task Test_LookupManager_ShouldReturnNull_WhenLookupIsInProgress()
    {
        var enrEntryRegistry = new EnrEntryRegistry();
        var enrRecord = new EnrFactory(enrEntryRegistry).CreateFromString("enr:-IS4QHCYrYZbAKWCBRlAy5zzaDZXJBGkcnh4MHcBFZntXNFrdvJjX04jRzjzCBOonrkTfj499SZuOh8R33Ls8RRcy5wBgmlkgnY0gmlwhH8AAAGJc2VjcDI1NmsxoQPKY0yuDUmstAHYpMa2_oxVtw0RW_QAdpzBQA8yWM0xOIN1ZHCCdl8", new IdentityVerifierV4());
        var targetNodeId = RandomUtility.GenerateRandomData(32);
        var nodeTableEntry = new NodeTableEntry(enrRecord, new IdentityVerifierV4());

        mockRoutingTable
            .Setup(x => x.GetClosestNodes(It.IsAny<byte[]>()))
            .Returns(new List<NodeTableEntry> { nodeTableEntry });

        var lookupManager = new LookupManager(
            mockRoutingTable.Object,
            mockPacketManager.Object,
            mockRequestManager.Object,
            mockLoggerFactory.Object,
            connectionOptions,
            tableOptions);

        var firstLookup = lookupManager.LookupAsync(targetNodeId);
        var secondLookup = lookupManager.LookupAsync(targetNodeId);
        await Task.WhenAny(firstLookup, secondLookup);

        Assert.IsNull(secondLookup.Result);
    }

}
