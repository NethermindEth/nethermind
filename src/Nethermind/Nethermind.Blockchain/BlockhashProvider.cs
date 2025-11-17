// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public class BlockhashProvider(
        IBlockhashCache blockhashCache,
        ISpecProvider specProvider,
        IWorldState worldState,
        ILogManager? logManager)
        : IBlockhashProvider
    {
        private const int MaxDepth = 256;
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
            long depth = current - number;
            if (number >= current || number < 0 || depth > MaxDepth)
            {
                if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
                return null;
            }

            return blockhashCache.GetHash(currentBlock, (int)depth)
                   ?? throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation");
        }
    }
}
