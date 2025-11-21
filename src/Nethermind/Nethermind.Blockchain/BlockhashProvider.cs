// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
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
        IWorldState worldState,
        ILogManager? logManager)
        : IBlockhashProvider
    {
        public const int MaxDepth = 256;
        private readonly IBlockhashStore _blockhashStore = new BlockhashStore(worldState);
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        private Hash256[]? _hashes;

        public Hash256? GetBlockhash(BlockHeader currentBlock, long number, IReleaseSpec spec)
        {
            if (spec.IsBlockHashInStateAvailable)
            {
                return _blockhashStore.GetBlockHashFromState(currentBlock, number, spec);
            }

            long current = currentBlock.Number;
            long depth = current - number;
            if (number >= current || number < 0 || depth > MaxDepth)
            {
                if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning null for {currentBlock.Number} -> {number}");
                return null;
            }

            Hash256[] hashes = _hashes;
            return hashes is not null
                ? hashes[depth]
                : blockhashCache.GetHash(currentBlock, (int)depth)
                  ?? throw new InvalidDataException("Hash cannot be found when executing BLOCKHASH operation");
        }

        public async Task Prefetch(BlockHeader currentBlock, CancellationToken token)
        {
            _hashes = null;
            _hashes = await blockhashCache.Prefetch(currentBlock, token);
        }
    }
}
