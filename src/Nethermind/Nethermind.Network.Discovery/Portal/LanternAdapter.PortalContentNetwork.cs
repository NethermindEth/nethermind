// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Lantern.Discv5.Enr;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Portal.Messages;

namespace Nethermind.Network.Discovery.Portal;

public partial class LanternAdapter
{
    private class PortalContentNetwork(
        LanternAdapter lanternAdapter,
        IKademlia<IEnr, byte[], LookupContentResult> kademlia,
        ILogger logger)
        : IPortalContentNetwork
    {

        public async Task<byte[]?> LookupContent(byte[] key, CancellationToken token)
        {
            Stopwatch sw = new Stopwatch();
            var result = await kademlia.LookupValue(key, token);
            logger.Info($"Lookup {key.ToHexString()} took {sw.Elapsed}");

            sw.Restart();

            if (result == null) return null;

            if (result.Payload != null) return result.Payload;

            Debug.Assert(result.ConnectionId != null);

            var asBytes = await lanternAdapter.DownloadContentFromUtp(result.NodeId, result.ConnectionId, token);
            logger.Info($"UTP download for {key.ToHexString()} took {sw.Elapsed}");
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

        public void AddSeed(IEnr node)
        {
            kademlia.SeedNode(node);
        }
    }
}
