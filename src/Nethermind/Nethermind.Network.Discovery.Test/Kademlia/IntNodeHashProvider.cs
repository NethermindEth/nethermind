// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Kademlia;

namespace Nethermind.Network.Discovery.Test.Kademlia;

internal sealed class IntNodeHashProvider : INodeHashProvider<int, int>
{
    public static readonly IntNodeHashProvider Instance = new();

    public int GetHash(int node) => node;
}
