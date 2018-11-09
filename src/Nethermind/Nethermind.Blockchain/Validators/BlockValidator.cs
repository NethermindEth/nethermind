/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;

namespace Nethermind.Blockchain.Validators
{
    public class BlockValidator : IBlockValidator
    {
        private readonly IHeaderValidator _headerValidator;
        private readonly ITransactionValidator _transactionValidator;
        private readonly IOmmersValidator _ommersValidator;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public BlockValidator(ITransactionValidator transactionValidator, IHeaderValidator headerValidator, IOmmersValidator ommersValidator, ISpecProvider specProvider, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _transactionValidator = transactionValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            _ommersValidator = ommersValidator ?? throw new ArgumentNullException(nameof(ommersValidator));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        }

        public bool ValidateSuggestedBlock(Block block)
        {
            if (!_ommersValidator.Validate(block.Header, block.Ommers))
            {
                _logger?.Debug($"Invalid block ({block.Hash}) - invalid ommers");
                return false;
            }

            Transaction[] txs = block.Transactions;
            for (int i = 0; i < txs.Length; i++)
            {
                if (!_transactionValidator.IsWellFormed(txs[i], _specProvider.GetSpec(block.Number)))
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.HashAndNumber)}) - invalid transaction ({txs[i].Hash})");
                    return false;
                }
            }

            Keccak txsRoot = block.CalculateTransactionsRoot();
            if (txsRoot != block.Header.TransactionsRoot)
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.HashAndNumber)}) tx root {txsRoot} != stated tx root {block.Header.TransactionsRoot}");
                return false;
            }

            if (block.Header.OmmersHash != Keccak.Compute(Rlp.Encode(block.Ommers)))
            {
                _logger?.Debug($"Invalid block ({block.ToString(Block.Format.HashAndNumber)}) - invalid ommers hash");
                return false;
            }

            bool blockHeaderValid = _headerValidator.Validate(block.Header);
            if (!blockHeaderValid)
            {
                if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.HashAndNumber)}) - invalid header");
                return false;
            }

            return true;
        }

        public bool ValidateProcessedBlock(Block processedBlock, Block suggestedBlock)
        {
            bool isValid = processedBlock.Header.Hash == suggestedBlock.Header.Hash;
            if (_logger != null && !isValid)
            {
                if (_logger.IsError) _logger.Error($"Processed block {processedBlock.ToString(Block.Format.Short)} is not valid");
                if(_logger.IsError) _logger.Error($"  hash {processedBlock.Hash} != stated hash {suggestedBlock.Hash}");
                
                if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed)
                {
                    if(_logger.IsError) _logger.Error($"  gas used {processedBlock.Header.GasUsed} != stated gas used {suggestedBlock.Header.GasUsed} ({processedBlock.Header.GasUsed - suggestedBlock.Header.GasUsed} difference)");
                }

                if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom)
                {
                    if(_logger.IsError) _logger.Error($"  bloom {processedBlock.Header.Bloom} != stated bloom {suggestedBlock.Header.Bloom}");
                }

                if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot)
                {
                    if(_logger.IsError) _logger.Error($"  receipts root {processedBlock.Header.ReceiptsRoot} != stated receipts root {suggestedBlock.Header.ReceiptsRoot}");
                }

                if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
                {
                    if(_logger.IsError) _logger.Error($"  state root {processedBlock.Header.StateRoot} != stated state root {suggestedBlock.Header.StateRoot}");
                }
            }

            return isValid;
        }
    }
}