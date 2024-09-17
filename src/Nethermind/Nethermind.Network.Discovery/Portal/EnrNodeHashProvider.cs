// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Lantern.Discv5.Enr;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;

namespace Nethermind.Network.Discovery.Portal;

public class EnrNodeHashProvider : INodeHashProvider<IEnr>
{
    public static EnrNodeHashProvider Instance = new EnrNodeHashProvider();

    private EnrNodeHashProvider()
    {
    }

    public ValueHash256 GetHash(IEnr node)
    {
        return new ValueHash256(node.NodeId);
    }
}
