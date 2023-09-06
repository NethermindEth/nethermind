// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class AccountRangeMessage : SnapMessageBase
    {
        public override int PacketType => SnapMessageCode.AccountRange;

        /// <summary>
        /// List of consecutive accounts from the trie
        /// </summary>
        public PathWithAccount[] PathsWithAccounts { get; set; }

        /// <summary>
        /// List of trie nodes proving the account range
        /// </summary>
        public byte[][] Proofs { get; set; }
    }
}
