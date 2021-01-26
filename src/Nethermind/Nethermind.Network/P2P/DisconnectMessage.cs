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

using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P
{
    public class DisconnectMessage : P2PMessage
    {
        public DisconnectMessage(DisconnectReason reason)
        {
            Reason = (int)reason;
        }
        
        public DisconnectMessage(int reason)
        {
            Reason = reason;
        }

        public override string Protocol => "p2p";
        public override int PacketType => P2PMessageCode.Disconnect;
        public int Reason { get; }

        public override string ToString() => $"Disconnect({Reason})";
    }
}
