// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Stats;

namespace Nethermind.Network
{
    public static class NodeStatsManagerExtension
    {
        public static void UpdateCurrentReputation(this INodeStatsManager nodeStatsManager, IEnumerable<Peer> peers) =>
            nodeStatsManager.UpdateCurrentReputation(peers.Where(p => p?.Node is not null).Select(p => p.Node));
    }
}
