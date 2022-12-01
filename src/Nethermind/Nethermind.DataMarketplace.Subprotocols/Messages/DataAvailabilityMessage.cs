// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataAvailabilityMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataAvailability;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public DataAvailability DataAvailability { get; }

        public DataAvailabilityMessage(Keccak depositId, DataAvailability dataAvailability)
        {
            DepositId = depositId;
            DataAvailability = dataAvailability;
        }
    }
}
