// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.RoutingTable;

public class NodeBucketItem(Node? node, DateTime lastContactTime)
{
    public Node? Node { get; } = node;

    public DateTime LastContactTime { get; private set; } = lastContactTime;

    public bool IsBonded(DateTime utcNow) => LastContactTime > utcNow - TimeSpan.FromDays(2);

    public void OnContactReceived() => LastContactTime = DateTime.UtcNow;

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is NodeBucketItem item && Node is not null)
        {
            return Node.IdHash.Equals(item.Node?.IdHash);
        }

        return false;
    }

    public override int GetHashCode() => Node?.GetHashCode() ?? 0;
}
