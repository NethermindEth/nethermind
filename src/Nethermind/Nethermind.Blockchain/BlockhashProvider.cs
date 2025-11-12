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
    public class BlockhashProvider(
        IBlockFinder blockTree,
        ISpecProvider specProvider,
        IWorldState worldState,
        ILogManager? logManager)
        : IBlockhashProvider
    {
        private const int MaxDepth = 256;
        private readonly IBlockFinder _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly IBlockhashStore _blockhashStore = new BlockhashStore(specProvider, worldState);
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number)
            => GetBlockhash(currentBlock, number, specProvider.GetSpec(currentBlock));

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
        {
            if (spec.IsBlockHashInStateAvailable)
            {
                return _blockhashStore.GetBlockHashFromState(currentBlock, number);
            }

            long current = currentBlock.Number;
            if (number >= current || number < current - Math.Min(current, MaxDepth))
            {
                return null;
            }

            BlockHeader header = _blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded) ??
                throw new InvalidDataException("Parent header cannot be found when executing BLOCKHASH operation");

            for (int i = 0; i < MaxDepth; i++)
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
