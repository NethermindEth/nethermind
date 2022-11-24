// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataAssetRemovedMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataAssetRemoved;
        public override string Protocol => "ndm";
        public Keccak DataAssetId { get; }

        public DataAssetRemovedMessage(Keccak dataAssetId)
        {
            DataAssetId = dataAssetId;
        }
    }
}
