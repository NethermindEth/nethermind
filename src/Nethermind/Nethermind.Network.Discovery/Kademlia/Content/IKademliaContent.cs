// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia.Content;

public interface IKademliaContent<in TContentKey, TContent>
{
    /// Initiate a full traversal for finding the value
    Task<TContent?> LookupValue(TContentKey id, CancellationToken token);
}
