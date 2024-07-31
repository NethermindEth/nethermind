// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Lantern.Discv5.Enr;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public class PortalContentNetwork(
    IUtpManager utpManager,
    IKademlia<IEnr, byte[], LookupContentResult> kademlia,
    IMessageSender<IEnr, byte[], LookupContentResult> messageSender,
    ILogger logger)
    : IPortalContentNetwork
{

    public async Task<byte[]?> LookupContent(byte[] key, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        token = cts.Token;

        Stopwatch sw = Stopwatch.StartNew();
        var result = await kademlia.LookupValue(key, token);
        logger.Info($"Lookup {key.ToHexString()} took {sw.Elapsed}");

        sw.Restart();

        if (result == null) return null;

        if (result.Payload != null) return result.Payload;

        Debug.Assert(result.ConnectionId != null);

        var asBytes = await utpManager.DownloadContentFromUtp(result.NodeId, result.ConnectionId.Value, token);
        logger.Info($"UTP download for {key.ToHexString()} took {sw.Elapsed}");
        return asBytes;
    }

    public async Task<byte[]?> LookupContentFrom(IEnr node, byte[] contentKey, CancellationToken token)
    {
        var content = await messageSender.FindValue(node, contentKey, token);
        if (!content.hasValue)
        {
            return null;
        }

        var value = content.value!;
        if (value.Payload != null) return value.Payload;

        var asBytes = await utpManager.DownloadContentFromUtp(node, value.ConnectionId!.Value, token);
        return asBytes;
    }

    public async Task Run(CancellationToken token)
    {
        await kademlia.Run(token);
    }

    public async Task Bootstrap(CancellationToken token)
    {
        await kademlia.Bootstrap(token);
    }

    public void AddOrRefresh(IEnr node)
    {
        kademlia.AddOrRefresh(node);
    }
}
