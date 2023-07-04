// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class ReceiptsMessage : Eth66Message<V63.Messages.ReceiptsMessage>
    {
        public ReceiptsMessage()
        {
        }

        public ReceiptsMessage(long requestId, V63.Messages.ReceiptsMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
