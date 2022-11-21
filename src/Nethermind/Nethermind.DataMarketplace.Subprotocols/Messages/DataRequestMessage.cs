// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataRequestMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataRequest;
        public override string Protocol => "ndm";
        public DataRequest DataRequest { get; }
        public uint ConsumedUnits { get; }

        public DataRequestMessage(DataRequest dataRequest, uint consumedUnits)
        {
            DataRequest = dataRequest;
            ConsumedUnits = consumedUnits;
        }
    }
}
