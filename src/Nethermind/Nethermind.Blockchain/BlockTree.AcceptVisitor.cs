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
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain
{
    public partial class BlockTree
    {
        public async Task Accept(IBlockTreeVisitor visitor, CancellationToken cancellationToken)
        {
            if (visitor.PreventsAcceptingNewBlocks)
            {
                BlockAcceptingNewBlocks();
            }
            
            try
            {
                long levelNumber = visitor.StartLevelInclusive;
                long blocksToVisit = visitor.EndLevelExclusive - visitor.StartLevelInclusive;
                for (long i = 0; i < blocksToVisit; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    ChainLevelInfo level = LoadLevel(levelNumber);
                    
                    LevelVisitOutcome visitOutcome = await visitor.VisitLevelStart(level, levelNumber, cancellationToken);
                    if ((visitOutcome & LevelVisitOutcome.DeleteLevel) == LevelVisitOutcome.DeleteLevel)
                    {
                        _chainLevelInfoRepository.Delete(levelNumber);
                        level = null;
                    }

                    if ((visitOutcome & LevelVisitOutcome.StopVisiting) == LevelVisitOutcome.StopVisiting)
                    {
                        break;
                    }

                    int numberOfBlocksAtThisLevel = level?.BlockInfos.Length ?? 0;
                    for (int blockIndex = 0; blockIndex < numberOfBlocksAtThisLevel; blockIndex++)
                    {
                        // if we delete blocks during the process then the number of blocks at this level will be falling and we need to adjust the index
                        Keccak hash = level!.BlockInfos[blockIndex - (numberOfBlocksAtThisLevel - level.BlockInfos.Length)].BlockHash;
                        Block block = FindBlock(hash, BlockTreeLookupOptions.None);
                        if (block == null)
                        {
                            BlockHeader header = FindHeader(hash, BlockTreeLookupOptions.None);
                            if (header == null)
                            {
                                if (await VisitMissing(visitor, hash, cancellationToken)) break;
                            }
                            else
                            {
                                if (await VisitHeader(visitor, header, cancellationToken)) break;
                            }
                        }
                        else
                        {
                            if (await VisitBlock(visitor, block, cancellationToken)) break;
                        }
                    }

                    visitOutcome = await visitor.VisitLevelEnd(level, levelNumber, cancellationToken);
                    if ((visitOutcome & LevelVisitOutcome.DeleteLevel) == LevelVisitOutcome.DeleteLevel)
                    {
                        _chainLevelInfoRepository.Delete(levelNumber);
                    }

                    levelNumber++;
                }

                RecalculateTreeLevels();

                string resultWord = cancellationToken.IsCancellationRequested ? "Canceled" : "Completed";

                if (_logger.IsDebug) _logger.Debug($"{resultWord} visiting blocks in DB at level {levelNumber} - best known {BestKnownNumber}");
            }
            finally
            {
                if (visitor.PreventsAcceptingNewBlocks)
                {
                    ReleaseAcceptingNewBlocks();
                }
            }
        }

        private static async Task<bool> VisitMissing(IBlockTreeVisitor visitor, Keccak hash, CancellationToken cancellationToken)
        {
            bool shouldContinue = await visitor.VisitMissing(hash, cancellationToken);
            if (!shouldContinue)
            {
                return true;
            }

            return false;
        }

        private static async Task<bool> VisitHeader(IBlockTreeVisitor visitor, BlockHeader header, CancellationToken cancellationToken)
        {
            HeaderVisitOutcome outcome = await visitor.VisitHeader(header, cancellationToken);
            if (outcome == HeaderVisitOutcome.StopVisiting)
            {
                return true;
            }

            return false;
        }

        private async Task<bool> VisitBlock(IBlockTreeVisitor visitor, Block block, CancellationToken cancellationToken)
        {
            BlockVisitOutcome blockVisitOutcome = await visitor.VisitBlock(block, cancellationToken);
            if ((blockVisitOutcome & BlockVisitOutcome.Suggest) == BlockVisitOutcome.Suggest)
            {
                // remnant after previous approach - we want to skip standard suggest processing and just invoke processor
                BestSuggestedHeader = block.Header;
                BestSuggestedBody = block;
                NewBestSuggestedBlock?.Invoke(this, new BlockEventArgs(block));
            }

            if ((blockVisitOutcome & BlockVisitOutcome.StopVisiting) == BlockVisitOutcome.StopVisiting)
            {
                return true;
            }

            return false;
        }
    }
}
