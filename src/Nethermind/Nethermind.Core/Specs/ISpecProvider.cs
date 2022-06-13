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

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// Provides details of enabled EIPs and other chain parameters at any chain height.
    /// </summary>
    public interface ISpecProvider
    {
        /// <summary>
        /// The merge block number is different from the rest forks because we don't know the merge block before it happens.
        /// This function handles change of the merge block
        /// </summary>
        void UpdateMergeTransitionInfo(long? blockNumber, UInt256? terminalTotalDifficulty = null);
        
        /// <summary>
        /// We have two different block numbers for merge transition:
        /// https://github.com/ethereum/EIPs/blob/d896145678bd65d3eafd8749690c1b5228875c39/EIPS/eip-3675.md#definitions
        /// 1.FORK_NEXT_VALUE (MergeForkId in chain spec) - we know it before the merge happens. It is included in TransitionsBlocks.
        /// It will affect fork_id calculation for networking.
        /// 2. The real merge block (ISpecProvider.MergeBlockNumber) - the real merge block number. We don't know it before the transition.
        /// It affects all post-merge logic, for example, difficulty opcode, post-merge block rewards.
        /// This block number doesn't affect fork_id calculation and it isn't included in ISpecProvider.TransitionsBlocks
        /// </summary>
        long? MergeBlockNumber { get; }
        
        UInt256? TerminalTotalDifficulty { get; }

        /// <summary>
        /// Retrieves the list of enabled EIPs at genesis block.
        /// </summary>
        IReleaseSpec GenesisSpec { get; }

        /// <summary>
        /// Block number at which DAO happens (only relevant for mainnet)
        /// </summary>
        long? DaoBlockNumber { get; }

        /// <summary>
        /// Unique identifier of the chain that allows to sign messages for the specified chain only.
        /// It is also used when verifying if sync peers are on the same chain.
        /// </summary>
        ulong ChainId { get; }

        /// <summary>
        /// All block numbers at which a change in spec (a fork) happens.
        /// </summary>
        long[] TransitionBlocks { get; }

        /// <summary>
        /// Resolves a spec for the given block number.
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <returns>A spec that is valid at the given chain height</returns>
        IReleaseSpec GetSpec(long blockNumber);
    }
}
