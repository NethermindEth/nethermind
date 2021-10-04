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

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// Provides details of enabled EIPs and other chain parameters at any chain height.
    /// </summary>
    public interface ISpecProvider
    {
        /// <summary>
        /// Retrieves the list of enabled EIPs at genesis block.
        /// </summary>
        IReleaseSpec GenesisSpec { get; }
        
        /// <summary>
        /// Retrieves a spec (list of enabled EIPs and some other properties) for the given chain height.
        /// </summary>
        /// <param name="blockNumber"></param>
        /// <returns></returns>
        IReleaseSpec GetSpec(long blockNumber);

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
    }
}
