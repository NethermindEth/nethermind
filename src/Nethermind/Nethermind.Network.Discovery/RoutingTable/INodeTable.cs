// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using static Nethermind.Network.Discovery.RoutingTable.NodeTable;

namespace Nethermind.Network.Discovery.RoutingTable;

public interface INodeTable
{
    void Initialize(PublicKey masterNodeKey);
    Node? MasterNode { get; }
    NodeBucket[] Buckets { get; }
    NodeAddResult AddNode(Node node);
    void ReplaceNode(Node nodeToRemove, Node nodeToAdd);
    void RefreshNode(Node node);

    /// <summary>
    /// GetClosestNodes to MasterNode
    /// </summary>
    ClosestNodesEnumerator GetClosestNodes();

    /// <summary>
    /// GetClosestNodes to provided Node
    /// </summary>
    ClosestNodesFromNodeEnumerator GetClosestNodes(byte[] nodeId);
    ClosestNodesFromNodeEnumerator GetClosestNodes(byte[] nodeId, int bucketSize);
}
