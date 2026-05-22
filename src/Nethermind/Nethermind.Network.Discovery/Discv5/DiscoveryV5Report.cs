// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

internal class DiscoveryV5Report
{
    int RecentlyChecked = 0;
    int TotalChecked = 0;

    public DiscoveryV5Report(IKademlia<PublicKey, Node> kademlia, ILogManager logManager, CancellationToken token)
    {
        ILogger logger = logManager.GetClassLogger<DiscoveryV5Report>();
        if (!logger.IsDebug)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                logger.Debug($"Nodes checked: {Interlocked.Exchange(ref RecentlyChecked, 0)}, in total {TotalChecked}. Kademlia table state: {kademlia.IterateNodes().Count()} nodes.");
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
