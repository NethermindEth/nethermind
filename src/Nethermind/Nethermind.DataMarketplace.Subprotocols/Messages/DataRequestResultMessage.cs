// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataRequestResultMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataRequestResult;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public DataRequestResult Result { get; }

        public DataRequestResultMessage(Keccak depositId, DataRequestResult result)
        {
            DepositId = depositId;
            Result = result;
        }
    }
}
