// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Validators
{
    public class BlockValidator : IBlockValidator
    {
        private readonly IHeaderValidator _headerValidator;
        private readonly ITxValidator _txValidator;
        private readonly IUnclesValidator _unclesValidator;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public BlockValidator(
            ITxValidator? txValidator,
            IHeaderValidator? headerValidator,
            IUnclesValidator? unclesValidator,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txValidator = txValidator ?? throw new ArgumentNullException(nameof(txValidator));
            _unclesValidator = unclesValidator ?? throw new ArgumentNullException(nameof(unclesValidator));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        }

        public bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle)
        {
            return _headerValidator.Validate(header, parent, isUncle);
        }

        public bool Validate(BlockHeader header, bool isUncle)
        {
            return _headerValidator.Validate(header, isUncle);
        }

        /// <summary>
        /// Suggested block validation runs basic checks that can be executed before going through the expensive EVM processing.
        /// </summary>
        /// <param name="block">A block to validate</param>
        /// <returns><value>True</value> if the <paramref name="block"/> is valid, otherwise <value>False</value></returns>
        public bool ValidateSuggestedBlock(Block block)
        {
            Transaction[] txs = block.Transactions;
            IReleaseSpec spec = _specProvider.GetSpec(block.Header);

            for (int i = 0; i < txs.Length; i++)
            {
                if (!_txValidator.IsWellFormed(txs[i], spec))
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - invalid transaction ({txs[i].Hash})");
                    return false;
                }
            }

            if (spec.MaximumUncleCount < block.Uncles.Length)
            {
                _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - uncle count is {block.Uncles.Length} (MAX: {spec.MaximumUncleCount})");
                return false;
            }

            if (!ValidateUnclesHashMatches(block))
            {
                _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - invalid uncles hash");
                return false;
            }

            if (!_unclesValidator.Validate(block.Header, block.Uncles))
            {
                _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - invalid uncles");
                return false;
            }

            bool blockHeaderValid = _headerValidator.Validate(block.Header);
            if (!blockHeaderValid)
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - invalid header");
                return false;
            }

            if (!ValidateTxRootMatchesTxs(block, out Keccak txRoot))
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) tx root {txRoot} != stated tx root {block.Header.TxRoot}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Processed block validation is comparing the block hashes (which include all other results).
        /// We only make exact checks on what is invalid if the hash is different.
        /// </summary>
        /// <param name="processedBlock">This should be the block processing result (after going through the EVM processing)</param>
        /// <param name="receipts">List of tx receipts from the processed block (required only for better diagnostics when the receipt root is invalid).</param>
        /// <param name="suggestedBlock">Block received from the network - unchanged.</param>
        /// <returns><value>True</value> if the <paramref name="processedBlock"/> is valid, otherwise <value>False</value></returns>
        public bool ValidateProcessedBlock(Block processedBlock, TxReceipt[] receipts, Block suggestedBlock)
        {
            bool isValid = processedBlock.Header.Hash == suggestedBlock.Header.Hash;
            if (!isValid)
            {
                if (_logger.IsError) _logger.Error($"Processed block {processedBlock.ToString(Block.Format.Short)} is not valid");
                if (_logger.IsError) _logger.Error($"  hash {processedBlock.Hash} != stated hash {suggestedBlock.Hash}");

                if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed)
                {
                    if (_logger.IsError) _logger.Error($"  gas used {processedBlock.Header.GasUsed} != stated gas used {suggestedBlock.Header.GasUsed} ({processedBlock.Header.GasUsed - suggestedBlock.Header.GasUsed} difference)");
                }

                if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom)
                {
                    if (_logger.IsError) _logger.Error($"  bloom {processedBlock.Header.Bloom} != stated bloom {suggestedBlock.Header.Bloom}");
                }

                if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot)
                {
                    if (_logger.IsError) _logger.Error($"  receipts root {processedBlock.Header.ReceiptsRoot} != stated receipts root {suggestedBlock.Header.ReceiptsRoot}");
                }

                if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
                {
                    if (_logger.IsError) _logger.Error($"  state root {processedBlock.Header.StateRoot} != stated state root {suggestedBlock.Header.StateRoot}");
                }

                for (int i = 0; i < processedBlock.Transactions.Length; i++)
                {
                    if (receipts[i].Error is not null && receipts[i].GasUsed == 0 && receipts[i].Error == "invalid")
                    {
                        if (_logger.IsError) _logger.Error($"  invalid transaction {i}");
                    }
                }
            }

            return isValid;
        }

        public static bool ValidateTxRootMatchesTxs(Block block, out Keccak txRoot)
        {
            txRoot = new TxTrie(block.Transactions).RootHash;
            return txRoot == block.Header.TxRoot;
        }

        public static bool ValidateUnclesHashMatches(Block block)
        {
            return block.Header.UnclesHash == UnclesHash.Calculate(block);
        }
    }
}
