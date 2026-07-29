// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Bootnode;

internal interface IBootnodeKademliaBucketSource
{
    void AppendSnapshot(List<BootnodeKademliaBucketSnapshot> snapshot);
}

internal sealed class BootnodeKademliaBucketSource(
    string protocol,
    IRoutingTable<Node, Hash256> routingTable) : IBootnodeKademliaBucketSource
{
    public void AppendSnapshot(List<BootnodeKademliaBucketSnapshot> snapshot)
    {
        int bucketIndex = 0;
        foreach (RoutingTableBucket<Node, Hash256> bucket in routingTable.IterateBuckets())
        {
            snapshot.Add(new BootnodeKademliaBucketSnapshot(protocol, bucketIndex, bucket.Distance, bucket.Prefix.ToString(), bucket.Count));
            bucketIndex++;
        }
    }
}

internal sealed class BootnodeKademliaBucketRegistry
{
    private readonly Lock _lock = new();
    private readonly List<IBootnodeKademliaBucketSource> _sources = [];

    public void Register(IBootnodeKademliaBucketSource source)
    {
        lock (_lock)
        {
            _sources.Add(source);
        }
    }

    public BootnodeKademliaBucketSnapshot[] CreateSnapshot()
    {
        IBootnodeKademliaBucketSource[] sources;
        lock (_lock)
        {
            sources = [.. _sources];
        }

        List<BootnodeKademliaBucketSnapshot> snapshot = new(sources.Length * 8);
        for (int i = 0; i < sources.Length; i++)
        {
            sources[i].AppendSnapshot(snapshot);
        }

        return [.. snapshot];
    }
}

internal readonly record struct BootnodeKademliaBucketSnapshot(
    string Protocol,
    int Bucket,
    int Depth,
    string Prefix,
    int Count);
