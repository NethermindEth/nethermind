// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class GetSubTreeRangeMessage : VerkleMessageBase
{
    public override int PacketType => VerkleMessageCode.GetSubTreeRange;

    public SubTreeRange SubTreeRange { get; set; }

    /// <summary>
    /// Soft limit at which to stop returning data
    /// </summary>
    public long ResponseBytes { get; set; }
}
