// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V2.Messages
{
    public class BlockAccessListsMessage(IByteArrayList? data) : SnapMessageBase
    {
        public override int PacketType => Snap2MessageCode.BlockAccessLists;

        public IByteArrayList BlockAccessLists { get; } = data ?? EmptyByteArrayList.Instance;

        public override void Dispose()
        {
            base.Dispose();
            BlockAccessLists.Dispose();
        }
    }
}
