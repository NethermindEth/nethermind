// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class GraceUnitsExceededMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.GraceUnitsExceeded;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public uint ConsumedUnits { get; }
        public uint GraceUnits { get; }

        public GraceUnitsExceededMessage(Keccak depositId, uint consumedUnits, uint graceUnits)
        {
            DepositId = depositId;
            ConsumedUnits = consumedUnits;
            GraceUnits = graceUnits;
        }
    }
}
