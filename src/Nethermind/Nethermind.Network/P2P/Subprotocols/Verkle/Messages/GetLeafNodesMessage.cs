// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Verkle;
using Nethermind.Verkle.Tree.Utils;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class GetLeafNodesMessage : VerkleMessageBase
{
    public override int PacketType => VerkleMessageCode.GetLeafNodes;

    /// <summary>
    /// Root hash of the verkle trie to serve
    /// </summary>
    public Hash256 RootHash { get; set; }

    /// <summary>
    /// Leaf node paths to retrieve, ordered sequentially
    /// </summary>
    public byte[][] Paths { get; set; }

    /// <summary>
    /// Soft limit at which to stop returning data
    /// </summary>
    public long Bytes { get; set; }
}
