// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class ConsumerAddressChangedMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.ConsumerAddressChanged;
        public override string Protocol => "ndm";
        public Address Address { get; }

        public ConsumerAddressChangedMessage(Address address)
        {
            Address = address;
        }
    }
}
