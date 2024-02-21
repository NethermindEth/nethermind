// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Blockchain
{
    public sealed class BlockHashProvider(IBlockTree blockTree, ILogManager? logManager)
        : IBlockHashProvider
    {
        private static readonly int _maxDepth = 256;
        private readonly IBlockTree _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

        public Hash256? GetBlockHash(BlockHeader currentBlock, in long number)
        {
            long current = currentBlock.Number;
            if (number >= current || number < current - Math.Min(current, _maxDepth))
            {
                return null;
            }

            bool isFastSyncSearch = false;

            BlockHeader? header = _blockTree.FindParentHeader(currentBlock, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            if (header is null)
            {
                ThrowInvalidDataException();
            }

            for (var i = 0; i < _maxDepth; i++)
            {
                if (number == header!.Number)
                {
                    if (_logger.IsTrace) _logger.Trace($"BLOCKHASH opcode returning {header.Number},{header.Hash} for {currentBlock.Number} -> {number}");
                    return header.Hash;
                }

                header = _blockTree.FindParentHeader(header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                if (header is null)
                {
                    ThrowInvalidDataException();
                }

                if (_blockTree.IsMainChain(header.Hash!) && !isFastSyncSearch)
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
                                ThrowInvalidOperationException();
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

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowInvalidDataException() =>
            throw new InvalidDataException("Parent header cannot be found when executing BLOCKHASH operation");

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowInvalidOperationException() =>
            throw new InvalidOperationException("Invoke fast blocks chain search");
    }
}
