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

namespace Nethermind.Blockchain.Validators
{
    public class BlockValidator : IBlockValidator
    {
        private readonly IBlockHeaderValidator _blockHeaderValidator;
        private readonly ITransactionValidator _transactionValidator;
        private readonly IOmmersValidator _ommersValidator;
        private readonly ILogger _logger;

        public BlockValidator(ITransactionValidator transactionValidator, IBlockHeaderValidator blockHeaderValidator, IOmmersValidator ommersValidator, ILogger logger)
        {
            _transactionValidator = transactionValidator;
            _ommersValidator = ommersValidator;
            _logger = logger;
            _blockHeaderValidator = blockHeaderValidator;
        }

        public bool ValidateSuggestedBlock(Block suggestedBlock)
        {
            if (!_ommersValidator.Validate(suggestedBlock.Header, suggestedBlock.Ommers))
            {
                return false;
            }

            foreach (Transaction transaction in suggestedBlock.Transactions)
            {
                if (!_transactionValidator.IsWellFormed(transaction))
                {
                    return false;
                }
            }

            // TODO it may not be needed here (computing twice?)
            if (suggestedBlock.Header.OmmersHash != Keccak.Compute(Rlp.Encode(suggestedBlock.Ommers)))
            {
                return false;
            }

            bool blockHeaderValid = _blockHeaderValidator.Validate(suggestedBlock.Header);
            if (!blockHeaderValid)
            {
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
                    _logger?.Log($"PROCESSED_GASUSED {processedBlock.Header.GasUsed} != SUGGESTED_GASUSED {suggestedBlock.Header.GasUsed}");
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