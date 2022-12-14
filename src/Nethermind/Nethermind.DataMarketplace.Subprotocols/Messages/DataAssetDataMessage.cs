// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataAssetDataMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataAssetData;
        public override string Protocol => "ndm";
        public Keccak DepositId { get; }
        public string Client { get; }
        public string Data { get; }
        public uint ConsumedUnits { get; }

        public DataAssetDataMessage(Keccak depositId, string client, string data, uint consumedUnits)
        {
            DepositId = depositId;
            Client = client;
            Data = data;
            ConsumedUnits = consumedUnits;
        }
    }
}
