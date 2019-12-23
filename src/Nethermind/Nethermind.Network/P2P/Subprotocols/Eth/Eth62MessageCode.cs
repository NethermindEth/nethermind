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

namespace Nethermind.Network.P2P.Subprotocols.Eth
{
    public static class Eth62MessageCode
    {
        public const int Status = 0x00;
        public const int NewBlockHashes = 0x01;
        public const int Transactions = 0x02;
        public const int GetBlockHeaders = 0x03;
        public const int BlockHeaders = 0x04;
        public const int GetBlockBodies = 0x05;
        public const int BlockBodies = 0x06;
        public const int NewBlock = 0x07;
    }
}