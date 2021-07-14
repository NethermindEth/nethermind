//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

namespace Nethermind.Network.P2P.Subprotocols.Eth.V66
{
    public class BlockHeadersMessage : P2PMessage
    {
        public override int PacketType { get; } = Eth66MessageCode.BlockHeaders;
        public override string Protocol { get; } = "eth";
        public long RequestId { get; set; }
        public Eth.V62.BlockHeadersMessage EthMessage { get; set; }
        
        public BlockHeadersMessage() 
        {
        }
        public BlockHeadersMessage(long requestId, Eth.V62.BlockHeadersMessage ethMessage)
        {
            RequestId = requestId;
            EthMessage = ethMessage;
        }
    }
}
