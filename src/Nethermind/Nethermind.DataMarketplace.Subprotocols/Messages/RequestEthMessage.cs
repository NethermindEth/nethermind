// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class RequestEthMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.RequestEth;
        public override string Protocol => "ndm";
        public Address Address { get; }
        public UInt256 Value { get; }

        public RequestEthMessage(Address address, UInt256 value)
        {
            Address = address;
            Value = value;
        }
    }
}
