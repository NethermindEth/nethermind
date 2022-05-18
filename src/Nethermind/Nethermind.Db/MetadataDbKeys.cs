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

namespace Nethermind.Db
{
    public static class MetadataDbKeys
    {
        public const int TerminalPoWHash = 1;
        public const int TerminalPoWNumber = 2;
        public const int FinalizedBlockHash = 3;
        public const int SafeBlockHash = 4;
        public const int BeaconSyncPivotHash = 5;
        public const int BeaconSyncPivotNumber = 6;
        public const int LowestInsertedBeaconHeaderHash = 7;
        public const int FirstPoSHash = 8;
    }
}
