// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

public interface INodeHealthTracker<TNode>
{
    /// <returns><c>true</c> when the node was newly added to the routing table.</returns>
    bool OnIncomingMessageFrom(TNode sender);
    void OnRequestFailed(TNode node);
}
