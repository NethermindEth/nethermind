// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.WireProtocol;
using Nethermind.Logging;

namespace Nethermind.Network.Discovery.Discv5;

internal class DiscoveryReport
{
    int RecentlyChecked = 0;
    int TotalChecked = 0;

    public DiscoveryReport(IDiscv5Protocol discv5Protocol, ILogManager logManager, CancellationToken token)
    {
        ILogger logger = logManager.GetClassLogger<DiscoveryReport>();
        if (!logger.IsDebug)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                logger.Debug($"Nodes checked: {Interlocked.Exchange(ref RecentlyChecked, 0)}, in total {TotalChecked}. Kademlia table state: {discv5Protocol.GetActiveNodes.Count()} active nodes, {discv5Protocol.GetAllNodes.Count()} all nodes.");
                await Task.Delay(10_000, token);
            }
        }, token);
    }

    public void NodeFound()
    {
        Interlocked.Increment(ref RecentlyChecked);
        Interlocked.Increment(ref TotalChecked);
    }
}
