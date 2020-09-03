//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.PeerView
{
    public class PeerInfoRow
    {
        public string ClientType { get; set; }
        public string Host { get; set; }
        public string LastSignal { get; set; }
        public string EthDetails { get; set; }
        public int Reputation { get; set; } = 100;

        public override string ToString()
        {
            return $"{Host.PadLeft(15)} {(ClientType ?? "").PadLeft(12)} {(EthDetails ?? "").PadLeft(5)} {LastSignal} {Reputation.ToString().PadLeft(6)}";
        }
    }
}