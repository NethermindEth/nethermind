// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv4;

public class PublicKeyKeyOperator : IKeyOperator<PublicKey, ValueHash256, Node>
{
    public PublicKey GetKey(Node node)
    {
        return node.Id;
    }

    public ValueHash256 GetKeyHash(PublicKey key)
    {
        return key.Hash;
    }

    public PublicKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth)
    {
        // Obviously, we can't generate this. So we just randomly pick something.
        // I guess we can brute force it if needed.
        Span<byte> randomBytes = new byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}
