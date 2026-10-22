// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class ReceiptsMessageSerializer(IZeroInnerMessageSerializer<V63.Messages.ReceiptsMessage> ethMessageSerializer)
        : Eth66MessageSerializer<ReceiptsMessage, V63.Messages.ReceiptsMessage>(ethMessageSerializer);
}
