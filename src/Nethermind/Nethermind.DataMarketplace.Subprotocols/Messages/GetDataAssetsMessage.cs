// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;

namespace Nethermind.DataMarketplace.Subprotocols.Messages
{
    public class GetDataAssetsMessage : P2PMessage
    {
        public override int PacketType { get; } = NdmMessageCode.GetDataAssets;
        public override string Protocol => "ndm";
    }
}
