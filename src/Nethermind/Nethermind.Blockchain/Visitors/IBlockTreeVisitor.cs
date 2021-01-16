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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Visitors
{
    public interface IBlockTreeVisitor
    {
        /// <summary>
        /// Gives a hint to block tree that accepting new blocks should be halted for the length of the visit.
        /// </summary>
        bool PreventsAcceptingNewBlocks { get; }
        
        /// <summary>
        /// First block tree level to visit
        /// </summary>
        long StartLevelInclusive { get; }

        /// <summary>
        /// Last block tree level to visit
        /// </summary>
        long EndLevelExclusive { get; }

        /// <summary>
        /// When new chain level is visited (and before its blocks are enumerated)
        /// </summary>
        /// <param name="chainLevelInfo">Chain level info with basic information about the tree level</param>
        /// <param name="levelNumber">Level (block) number</param>
        /// <param name="cancellationToken"></param>
        /// <returns><value>false</value> if the visitor wants to stop visiting remaining levels, otherwise <value>true</value></returns>
        Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken);

        /// <summary>
        /// If the block hash is defined on the chain level but is missing from the database.
        /// </summary>
        /// <param name="hash">Hash of the missing block</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<bool> VisitMissing(Keccak hash, CancellationToken cancellationToken);

        /// <summary>
        /// If the block hash is defined on the chain level and only header is available but not block body 
        /// </summary>
        /// <param name="header"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken);

        /// <summary>
        /// If the block hash is defined on the chain level and both header and body are in the database
        /// </summary>
        /// <param name="block"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken);

        /// <summary>
        /// So the visitor can execute any logic after all block / headers have been visited for the level and before the next level is visited
        /// </summary>
        /// <param name="chainLevelInfo">Chain level info with basic information about the tree level</param>
        /// <param name="levelNumber">Level (block) number</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<LevelVisitOutcome> VisitLevelEnd(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken);
    }
}
