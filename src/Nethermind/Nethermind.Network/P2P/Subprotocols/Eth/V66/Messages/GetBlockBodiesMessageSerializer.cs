// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages
{
    public class GetBlockBodiesMessageSerializer : Eth66MessageSerializer<GetBlockBodiesMessage, V62.Messages.GetBlockBodiesMessage>
    {
        public GetBlockBodiesMessageSerializer() : base(new V62.Messages.GetBlockBodiesMessageSerializer())
        {
        }
    }
}
