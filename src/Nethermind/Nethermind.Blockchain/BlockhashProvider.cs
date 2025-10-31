// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public class BlockhashProvider : IBlockhashProvider
    {
        private static readonly int _maxDepth = 256;
        private readonly IBlockFinder _blockTree;
        private readonly ISpecProvider _specProvider;
        private readonly IBlockAncestorTracker _ancestorTracker;
        private readonly IBlockhashStore _blockhashStore;
        private readonly ILogger _logger;

        private readonly bool _disableFallback;

        public BlockhashProvider(IBlockFinder blockTree, ISpecProvider specProvider, IWorldState worldState, IBlockAncestorTracker ancestorTracker, ILogManager? logManager, bool disableFallback = false)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _ancestorTracker = ancestorTracker ?? throw new ArgumentNullException(nameof(ancestorTracker));
            _specProvider = specProvider;
            _blockhashStore = new BlockhashStore(specProvider, worldState);
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            // Used for testing to make sure ancestors are valid. Will definitely fail on processing block head-_maxDepth
            _disableFallback = disableFallback;
        }

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number)
            => GetBlockhash(currentBlock, number, _specProvider.GetSpec(currentBlock));

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec? spec)
        {
            if (spec.IsBlockHashInStateAvailable)
            {
                return _blockhashStore.GetBlockHashFromState(currentBlock, number);
            }

            long current = currentBlock.Number;
            if (number >= current || number < current - Math.Min(current, _maxDepth) || number < 0)
            {
                return null;
            }

            long depth = currentBlock.Number - number - 1;
            if (depth == 0)
            {
                return currentBlock.ParentHash;
            }

            // Note: current block hash is not gonna be in the ancestor tracker, so we use the parent hash
            Hash256? blockHash = _ancestorTracker.GetAncestor(currentBlock.ParentHash, depth - 1);
            if (blockHash != null)
            {
                return blockHash;
            }

            if (_disableFallback)
            {
                throw new InvalidDataException(
                    $"Ancestor not processed for {number}. Current block {currentBlock.ToString(BlockHeader.Format.Short)}. Fallback disabled.");
            }

            return GetBlockHashFromNonHeadParent(currentBlock, number);
        }

        private Hash256 GetBlockHashFromNonHeadParent(BlockHeader currentBlock, long number)
        {
            BlockHeader header = _blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded) ??
                throw new InvalidDataException("Parent header cannot be found when executing BLOCKHASH operation");

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
            }

            if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
            return null;
        }
    }
}
