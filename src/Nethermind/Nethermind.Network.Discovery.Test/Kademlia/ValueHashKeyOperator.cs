// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class ValueHashKeyOperator<TNode>(Func<TNode, ValueHash256> getKey) : IKeyOperator<ValueHash256, TNode, Hash256>
{
    public static Hash256 ToHash(ValueHash256 hash) => hash.ToHash256();

    public static ValueHash256 ToValueHash(Hash256 hash) => hash.ValueHash256;

    public ValueHash256 GetKey(TNode node) => getKey(node);

    public Hash256 GetKeyHash(ValueHash256 key) => ToHash(key);

    public ValueHash256 CreateRandomKeyAtDistance(Hash256 nodePrefix, int depth)
        => ToValueHash(Hash256KademliaDistance.Instance.GetRandomHashAtDistance(nodePrefix, depth));
}
