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

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public static class SnapMessageCode
    {
        public const int GetAccountRange = 0x00;
        public const int AccountRange = 0x01;
        public const int GetStorageRanges = 0x02;
        public const int StorageRanges = 0x03;
        public const int GetByteCodes = 0x04;
        public const int ByteCodes = 0x05;
        public const int GetTrieNodes = 0x06;
        public const int TrieNodes = 0x07;
    }
}
