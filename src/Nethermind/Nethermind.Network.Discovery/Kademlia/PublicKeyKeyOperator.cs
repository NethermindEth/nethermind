// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Kademlia;

public class PublicKeyKeyOperator : IKeyOperator<PublicKey, Node, Hash256>
{
    public PublicKey GetKey(Node node) => node.Id;

    public Hash256 GetKeyHash(PublicKey key) => key.Hash;

    /// <summary>
    /// Creates a random discv4 lookup target.
    /// </summary>
    /// <remarks>
    /// Discv4 FINDNODE carries a public key, while bucket refresh starts from a desired node-id hash prefix.
    /// Constructing a public key whose Keccak hash lands in that prefix is not practical, so this uses a random
    /// 64-byte target and treats discv4 bucket refresh as best-effort sampling.
    /// </remarks>
    public PublicKey CreateRandomKeyAtDistance(Hash256 nodePrefix, int depth)
    {
        Span<byte> randomBytes = new byte[64];
        Random.Shared.NextBytes(randomBytes);
        return new PublicKey(randomBytes);
    }
}
