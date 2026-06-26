// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network
{
    public static class NodeStatsManagerExtension
    {
        public static void UpdateCurrentReputation(this INodeStatsManager nodeStatsManager, IEnumerable<Peer?> peers) =>
            nodeStatsManager.UpdateCurrentReputation(SelectNodes(peers));

        private static IEnumerable<Node> SelectNodes(IEnumerable<Peer?> peers)
        {
            foreach (Peer? peer in peers)
            {
                if (peer?.Node is Node node)
                {
                    yield return node;
                }
            }
        }
    }
}
