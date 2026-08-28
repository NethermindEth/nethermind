// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class ValueHashKeyOperator<TNode>(Func<TNode, ValueHash256> getKey) : IKeyOperator<ValueHash256, TNode, ValueHash256>
{
    public ValueHash256 GetKey(TNode node) => getKey(node);

    public ValueHash256 GetKeyHash(ValueHash256 key) => key;

    public ValueHash256 CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
        => ValueHash256KademliaDistance.Instance.GetRandomHashAtDistance(nodePrefix, depth, Random.Shared);
}
