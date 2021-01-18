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

using Nethermind.Int256;

namespace Nethermind.Consensus.Clique
{
    internal static class Clique
    {
        /// <summary>
        /// Number of blocks between the checkpoints
        /// </summary>
        public const int CheckpointInterval = 1024;
        
        /// <summary>
        /// Number of blocks within the Clique epoch
        /// </summary>
        public const int DefaultEpochLength = 30000;

        /// <summary>
        /// Snapshots cache size
        /// </summary>
        public const int InMemorySnapshots = 128;
        
        /// <summary>
        /// Signatures cache size
        /// </summary>
        public const int InMemorySignatures = 4096;

        /// <summary>
        /// Delay time before producing out-of-turn block
        /// </summary>
        public const int WiggleTime = 500;

        /// <summary>
        /// Length of extra vanity within the extra data
        /// </summary>
        public const int ExtraVanityLength = 32;
        
        /// <summary>
        /// Length of an extra seal within the extra data
        /// </summary>
        public const int ExtraSealLength = 65;

        /// <summary>
        /// Nonce to set on the block header when adding a vote
        /// </summary>
        public const ulong NonceAuthVote = ulong.MaxValue;
        
        /// <summary>
        /// Nonce to set on the block header when removing a previous signer vote
        /// </summary>
        public const ulong NonceDropVote = 0UL;

        /// <summary>
        /// Difficulty of a block produced by a signer in turn
        /// </summary>
        public static UInt256 DifficultyInTurn = 2;
        
        /// <summary>
        /// Difficulty of a block produced by an alternative signer (out of turn)
        /// </summary>
        public static UInt256 DifficultyNoTurn = UInt256.One;
    }
}
