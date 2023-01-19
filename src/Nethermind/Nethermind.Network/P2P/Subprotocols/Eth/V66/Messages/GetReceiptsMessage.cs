// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetReceiptsMessage : Eth66Message<V63.Messages.GetReceiptsMessage>
    {
        public GetReceiptsMessage()
        {
        }

        public GetReceiptsMessage(long requestId, V63.Messages.GetReceiptsMessage ethMessage) : base(requestId, ethMessage)
        {
        }
    }
}
