// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Snap;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetAccountRangeMessage : SnapMessageBase
    {
        public override int PacketType => Snap1MessageCode.GetAccountRange;

        public AccountRange AccountRange { get; set; }

        /// <summary>
        /// Soft limit at which to stop returning data
        /// </summary>
        public long ResponseBytes { get; set; }
    }
}
