// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery;

public interface INodesLocator
{
    /// <summary>
    /// locate nodes for master node
    /// </summary>
    Task LocateNodesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// locate nodes for specified node id
    /// </summary>
    Task LocateNodesAsync(byte[] searchedNodeId, CancellationToken cancellationToken);

    void Initialize(Node masterNode);
}
