// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class InvalidDataMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.InvalidData;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public InvalidDataReason Reason { get; }

        public InvalidDataMessage(Keccak depositId, InvalidDataReason reason)
        {
            DepositId = depositId;
            Reason = reason;
        }
    }
}
