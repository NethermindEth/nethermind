// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class IdentityNodeHashProvider : INodeHashProvider<ValueHash256>
{
    public static readonly IdentityNodeHashProvider Instance = new();

    public static KademliaHash ToKademliaHash(ValueHash256 hash) => KademliaHash.FromBytes(hash.BytesAsSpan);

    public KademliaHash GetHash(ValueHash256 node) => ToKademliaHash(node);
}
