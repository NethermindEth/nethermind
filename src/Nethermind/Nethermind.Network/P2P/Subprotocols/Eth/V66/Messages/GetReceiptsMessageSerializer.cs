// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetReceiptsMessageSerializer : Eth66MessageSerializer<GetReceiptsMessage, V63.Messages.GetReceiptsMessage>
    {
        public GetReceiptsMessageSerializer() : base(new V63.Messages.GetReceiptsMessageSerializer())
        {
        }
    }
}
