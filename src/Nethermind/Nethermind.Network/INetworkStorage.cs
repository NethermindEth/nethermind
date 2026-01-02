// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Config;
using Nethermind.Core.Crypto;

namespace Nethermind.Network
{
    public interface INetworkStorage
    {
        NetworkNode[] GetPersistedNodes();
        int PersistedNodesCount { get; }

        void UpdateNode(NetworkNode node);
        void UpdateNodes(IEnumerable<NetworkNode> nodes);
        void RemoveNode(PublicKey nodeId);
        void StartBatch();
        void Commit();
        bool AnyPendingChange();
    }
}
