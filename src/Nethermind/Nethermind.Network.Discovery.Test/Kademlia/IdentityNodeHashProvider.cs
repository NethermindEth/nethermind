// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class IdentityNodeHashProvider : INodeHashProvider<ValueHash256, Hash256>
{
    public static readonly IdentityNodeHashProvider Instance = new();

    public static Hash256 ToHash(ValueHash256 hash) => hash.ToHash256();

    public Hash256 GetHash(ValueHash256 node) => ToHash(node);
}
