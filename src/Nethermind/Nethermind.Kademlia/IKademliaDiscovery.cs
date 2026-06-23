// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Kademlia;

/// <summary>
/// Produces active Kademlia lookup candidates without owning protocol-specific peer admission.
/// </summary>
/// <typeparam name="TKey">The protocol-specific lookup key type.</typeparam>
/// <typeparam name="TNode">The protocol-specific node/contact type.</typeparam>
public interface IKademliaDiscovery<TKey, TNode>
{
    /// <summary>
    /// Runs active random lookup jobs and streams nodes discovered by those lookups.
    /// </summary>
    /// <param name="concurrentDiscoveryJobs">Number of active lookup jobs to run.</param>
    /// <param name="lookupResultLimit">Maximum number of candidates requested from each lookup.</param>
    /// <param name="token">Cancellation token that stops active discovery.</param>
    IAsyncEnumerable<TNode> DiscoverNodes(int concurrentDiscoveryJobs, int lookupResultLimit, CancellationToken token);
}
