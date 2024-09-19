// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia.Content;

/// <summary>
/// This interface extend <see cref="IKademlia{TNode}"/> with the ability to lookup content.
/// </summary>
/// <typeparam name="TContentKey"></typeparam>
/// <typeparam name="TContent"></typeparam>
public interface IKademliaContent<in TContentKey, TContent>
{
    /// <summary>
    /// Initiate a full network traversal for finding the value specified by TContent.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<TContent?> LookupValue(TContentKey id, CancellationToken token);
}
