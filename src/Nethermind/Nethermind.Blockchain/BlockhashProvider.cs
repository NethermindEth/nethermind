// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        private readonly IBlockhashStore _blockhashStore;
        private readonly ILogger _logger;

        public BlockhashProvider(IBlockFinder blockTree, ISpecProvider specProvider, IWorldState worldState, ILogManager? logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _specProvider = specProvider;
            _blockhashStore = new BlockhashStore(specProvider, worldState);
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
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
            if (number >= current || number < current - Math.Min(current, _maxDepth))
            {
                return null;
            }

            return _blockTree.FindBlockHash(number);
        }
    }
}
