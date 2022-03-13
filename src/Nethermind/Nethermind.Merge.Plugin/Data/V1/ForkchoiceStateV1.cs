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

using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Data.V1
{
    public class ForkchoiceStateV1
    {
        public ForkchoiceStateV1(Keccak headBlockHash, Keccak finalizedBlockHash, Keccak safeBlockHash)
        {
            HeadBlockHash = headBlockHash;
            FinalizedBlockHash = finalizedBlockHash;
            SafeBlockHash = safeBlockHash;
        }
        public Keccak HeadBlockHash { get; set; }
        
        public Keccak SafeBlockHash { get; set; }
        public Keccak FinalizedBlockHash { get; set; }

        public override string ToString()
        {
            return $"ForkchoiceState: ({nameof(HeadBlockHash)}: {HeadBlockHash}, {nameof(SafeBlockHash)}: {SafeBlockHash}, {nameof(FinalizedBlockHash)}: {FinalizedBlockHash})";
        }
    }
}
