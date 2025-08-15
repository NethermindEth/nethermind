using Lantern.Discv5.Enr;
using Lantern.Discv5.WireProtocol.Identity;
using Lantern.Discv5.WireProtocol.Messages;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Table;

public class TableManager(IPacketReceiver packetReceiver,
        IPacketManager packetManager,
        IIdentityManager identityManager,
        ILookupManager lookupManager,
        IRoutingTable routingTable,
        IEnrFactory enrFactory,
        ILoggerFactory loggerFactory,
        ICancellationTokenSourceWrapper cts,
        IGracefulTaskRunner taskRunner,
        TableOptions tableOptions)
    : ITableManager
{
    private readonly ILogger<TableManager> _logger = loggerFactory.CreateLogger<TableManager>();
    private Task? _refreshTask;
    private Task? _pingTask;

    public async Task InitAsync()
    {
        _logger.LogInformation("Starting TableManagerAsync");

        await InitFromBootstrapNodesAsync();

        _refreshTask = taskRunner.RunWithGracefulCancellationAsync(RefreshBucketsAsync, "RefreshBuckets", cts.GetToken());
        _pingTask = taskRunner.RunWithGracefulCancellationAsync(PingNodeAsync, "PingNode", cts.GetToken());
    }

    public async Task StopTableManagerAsync()
    {
        _logger.LogInformation("Stopping TableManagerAsync");
        cts.Cancel();

        await Task.WhenAll(_refreshTask!, _pingTask!).ConfigureAwait(false);

        if (cts.IsCancellationRequested())
        {
            _logger.LogInformation("TableManagerAsync was canceled gracefully");
        }
    }

    public async Task InitFromBootstrapNodesAsync()
    {
        if (routingTable.GetNodesCount() == 0)
        {
            _logger.LogInformation("Initialising from bootstrap ENRs");

            var bootstrapEnrs = routingTable.TableOptions.BootstrapEnrs
                .Select(enr => enrFactory.CreateFromString(enr, identityManager.Verifier))
                .ToArray();

            if (bootstrapEnrs.Length == 0)
            {
                _logger.LogWarning("No bootstrap ENRs found");
                return;
            }

            foreach (var bootstrapEnr in bootstrapEnrs)
            {
                try
                {
                    await packetReceiver.SendPingAsync(bootstrapEnr);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending packet to bootstrap ENR: {BootstrapEnr} {ex} {stack}", bootstrapEnr, ex.Message, ex.StackTrace);
                }
            }
        }
    }

    public async Task RefreshBucketsAsync(CancellationToken token)
    {
        _logger.LogInformation("Starting RefreshBucketsAsync");

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(tableOptions.RefreshIntervalMilliseconds, token).ConfigureAwait(false);
            await RefreshBucket().ConfigureAwait(false);
        }
    }

    public async Task PingNodeAsync(CancellationToken token)
    {
        _logger.LogInformation("Starting PingNodeAsync");

        while (!token.IsCancellationRequested)
        {
            await Task.Delay(tableOptions.PingIntervalMilliseconds, token).ConfigureAwait(false);

            if (routingTable.GetNodesCount() is 0)
            {
                await InitFromBootstrapNodesAsync();
                continue;
            }

            var targetNodeId = RandomUtility.GenerateRandomData(PacketConstants.NodeIdSize);
            var nodeEntry = routingTable.GetClosestNodes(targetNodeId).FirstOrDefault();

            if (nodeEntry == null)
                continue;

            await packetManager.SendPacket(nodeEntry.Record, MessageType.Ping, false).ConfigureAwait(false);
        }
    }

    public async Task RefreshBucket()
    {
        var targetNodeId = routingTable.GetLeastRecentlySeenNode();

        if (targetNodeId == null)
            return;

        var closestNodes = await lookupManager.LookupAsync(targetNodeId.Id);

        if (closestNodes != null)
        {
            foreach (var node in closestNodes)
            {
                routingTable.UpdateFromEnr(node);
            }
        }
    }
}
