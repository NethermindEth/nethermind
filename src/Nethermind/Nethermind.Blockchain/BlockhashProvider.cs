// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Blockchain
{
    public class BlockhashProvider : IBlockhashProvider
    {
        private static readonly int _maxDepth = 256;
        private readonly IBlockFinder _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly IWorldState _worldState;
        private readonly IBlockhashStore _blockhashStore;
        private readonly ILogger _logger;

        public BlockhashProvider(IBlockFinder blockTree, ISpecProvider specProvider, IWorldState worldState, ILogManager? logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _specProvider = specProvider;
            _worldState = worldState;
            _blockhashStore = new Blocks.BlockhashStore(blockTree, specProvider, worldState);
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public Hash256? GetBlockhash(BlockHeader currentBlock, in long number)
        {
            IReleaseSpec? spec = _specProvider.GetSpec(currentBlock);

            if (spec.IsBlockHashInStateAvailable)
            {
                return _blockhashStore.GetBlockHashFromState(currentBlock, number);
            }

            long current = currentBlock.Number;
            if (number >= current || number < current - Math.Min(current, _maxDepth))
            {
                return null;
            }

            bool isFastSyncSearch = false;

            BlockHeader header = _blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                throw new InvalidDataException("Parent header cannot be found when executing BLOCKHASH operation");
            }

            for (var i = 0; i < _maxDepth; i++)
            {
                if (number == header.Number)
                {
                    if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning {header.Number},{header.Hash} for {currentBlock.Number} -> {number}");
                    return header.Hash;
                }

                header = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (header is null)
                {
                    throw new InvalidDataException("Parent header cannot be found when executing BLOCKHASH operation");
                }

                if (_blockTree.IsMainChain(header.Hash) && !isFastSyncSearch)
                {
                    try
                    {
                        BlockHeader currentHeader = header;
                        header = _blockTree.FindHeader(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                        if (header is null)
                        {
                            isFastSyncSearch = true;
                            header = currentHeader;
                        }
                        else
                        {
                            if (!_blockTree.IsMainChain(header))
                            {
                                header = currentHeader;
                                throw new InvalidOperationException("Invoke fast blocks chain search");
                            }
                        }
                    }
                    catch (InvalidOperationException) // fast sync during the first 256 blocks after the transition
                    {
                        isFastSyncSearch = true;
                    }
                }
            }

            if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
            return null;
        }
    }
}
