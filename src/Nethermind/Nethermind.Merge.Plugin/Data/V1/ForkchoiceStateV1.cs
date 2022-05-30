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
    /// <summary>
    /// Arguments to engine_ForkChoiceUpdate
    ///
    /// <seealso cref="https://github.com/ethereum/execution-apis/blob/main/src/engine/specification.md#forkchoicestatev1"/>
    /// </summary>
    public class ForkchoiceStateV1
    {
        public ForkchoiceStateV1(Keccak headBlockHash, Keccak finalizedBlockHash, Keccak safeBlockHash)
        {
            HeadBlockHash = headBlockHash;
            FinalizedBlockHash = finalizedBlockHash;
            SafeBlockHash = safeBlockHash;
        }
        
        /// <summary>
        /// Hash of the head of the canonical chain.
        /// </summary>
        public Keccak HeadBlockHash { get; set; }
        
        /// <summary>
        /// Safe block hash of the canonical chain under certain synchrony and honesty assumptions. This value MUST be either equal to or an ancestor of headBlockHash.
        /// </summary>
        /// <remarks>Can be <see cref="Keccak.Zero"/> when transition block is not finalized yet.</remarks>
        public Keccak SafeBlockHash { get; set; }
        
        /// <summary>
        /// Hash of the most recent finalized block
        /// </summary>
        /// <remarks>Can be <see cref="Keccak.Zero"/> when transition block is not finalized yet.</remarks>
        public Keccak FinalizedBlockHash { get; set; }

        public override string ToString()
        {
            return $"ForkchoiceState: ({nameof(HeadBlockHash)}: {HeadBlockHash}, {nameof(SafeBlockHash)}: {SafeBlockHash}, {nameof(FinalizedBlockHash)}: {FinalizedBlockHash})";
        }
    }
}
