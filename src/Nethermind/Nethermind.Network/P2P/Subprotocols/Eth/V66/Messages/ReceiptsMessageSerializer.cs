// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class ReceiptsMessageSerializer : Eth66MessageSerializer<ReceiptsMessage, V63.Messages.ReceiptsMessage>
    {
        public ReceiptsMessageSerializer(IZeroInnerMessageSerializer<V63.Messages.ReceiptsMessage> ethMessageSerializer) : base(ethMessageSerializer)
        {
        }
    }
}
