// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class DataAssetStateChangedMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.DataAssetStateChanged;
        public override string Protocol => "ndm";
        public Keccak DataAssetId { get; }
        public DataAssetState State { get; }

        public DataAssetStateChangedMessage(Keccak dataAssetId, DataAssetState state)
        {
            DataAssetId = dataAssetId;
            State = state;
        }
    }
}
