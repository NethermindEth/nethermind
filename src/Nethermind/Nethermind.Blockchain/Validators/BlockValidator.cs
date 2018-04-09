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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
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

        public BlockValidator(ITransactionValidator transactionValidator, IHeaderValidator headerValidator, IOmmersValidator ommersValidator, ISpecProvider specProvider, ILogger logger)
        {
            _transactionValidator = transactionValidator;
            _ommersValidator = ommersValidator;
            _specProvider = specProvider;
            _logger = logger;
            _headerValidator = headerValidator;
        }

        public bool ValidateSuggestedBlock(Block suggestedBlock)
        {
            if (!_ommersValidator.Validate(suggestedBlock.Header, suggestedBlock.Ommers))
            {
                _logger?.Log($"Invalid block ({suggestedBlock.Hash}) - invalid ommers");
                return false;
            }

            foreach (Transaction transaction in suggestedBlock.Transactions)
            {
                if (!_transactionValidator.IsWellFormed(transaction, _specProvider.GetSpec(suggestedBlock.Number)))
                {
                    _logger?.Log($"Invalid block ({suggestedBlock.Hash}) - invalid transaction ({transaction.Hash})");
                    return false;
                }
            }

            // TODO it may not be needed here (computing twice?)
            if (suggestedBlock.Header.OmmersHash != Keccak.Compute(Rlp.Encode(suggestedBlock.Ommers)))
            {
                _logger?.Log($"Invalid block ({suggestedBlock.Hash}) - invalid ommers hash");
                return false;
            }

            bool blockHeaderValid = _headerValidator.Validate(suggestedBlock.Header);
            if (!blockHeaderValid)
            {
                _logger?.Log($"Invalid block ({suggestedBlock.Hash}) - invalid header");
                return false;
            }

            return true;
        }

        public bool ValidateProcessedBlock(Block processedBlock, Block suggestedBlock)
        {
            bool isValid= processedBlock.Header.Hash == suggestedBlock.Header.Hash;
            if (_logger != null && !isValid)
            {
                if (processedBlock.Header.GasUsed != suggestedBlock.Header.GasUsed)
                {
                    _logger?.Log($"PROCESSED_GASUSED {processedBlock.Header.GasUsed} != SUGGESTED_GASUSED {suggestedBlock.Header.GasUsed} ({processedBlock.Header.GasUsed - suggestedBlock.Header.GasUsed} difference)");
                }
                
                if (processedBlock.Header.Bloom != suggestedBlock.Header.Bloom)
                {
                    _logger?.Log($"PROCESSED_BLOOM {processedBlock.Header.Bloom} != SUGGESTED_BLOOM {suggestedBlock.Header.Bloom}");
                }
                
                if (processedBlock.Header.ReceiptsRoot != suggestedBlock.Header.ReceiptsRoot)
                {
                    _logger?.Log($"PROCESSED_RECEIPTS {processedBlock.Header.ReceiptsRoot} != SUGGESTED_RECEIPTS {suggestedBlock.Header.ReceiptsRoot}");
                }
                
                if (processedBlock.Header.StateRoot != suggestedBlock.Header.StateRoot)
                {
                    _logger?.Log($"PROCESSED_STATE {processedBlock.Header.StateRoot} != SUGGESTED_STATE {suggestedBlock.Header.StateRoot}");
                }
            }

            return isValid;
        }
    }
}