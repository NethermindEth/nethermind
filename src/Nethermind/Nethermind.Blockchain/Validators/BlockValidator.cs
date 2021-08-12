//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State.Proofs;
using Nethermind.TxPool;

namespace Nethermind.Blockchain.Validators
{
    public class BlockValidator : IBlockValidator
    {
        private readonly IHeaderValidator _headerValidator;
        private readonly ITxValidator _txValidator;
        private readonly IOmmersValidator _ommersValidator;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;

        public BlockValidator(ITxValidator? txValidator, IHeaderValidator? headerValidator, IOmmersValidator? ommersValidator, ISpecProvider? specProvider, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _txValidator = txValidator ?? throw new ArgumentNullException(nameof(headerValidator));
            _ommersValidator = ommersValidator ?? throw new ArgumentNullException(nameof(ommersValidator));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _headerValidator = headerValidator ?? throw new ArgumentNullException(nameof(headerValidator));
        }

        public bool ValidateHash(BlockHeader header)
        {
            return _headerValidator.ValidateHash(header);
        }
        
        public bool Validate(BlockHeader header, BlockHeader? parent, bool isOmmer)
        {
            return _headerValidator.Validate(header, parent, isOmmer);
        }

        public bool Validate(BlockHeader header, bool isOmmer)
        {
            return _headerValidator.Validate(header, isOmmer);
        }

        /// <summary>
        /// Suggested block validation runs basic checks that can be executed before going through the expensive EVM processing. 
        /// </summary>
        /// <param name="block">A block to validate</param>
        /// <returns><value>True</value> if the <paramref name="block"/> is valid, otherwise <value>False</value></returns>
        public bool ValidateSuggestedBlock(Block block)
        {
            Transaction[] txs = block.Transactions;
            IReleaseSpec spec = _specProvider.GetSpec(block.Number);
            
            for (int i = 0; i < txs.Length; i++)
            {
                if (!_txValidator.IsWellFormed(txs[i], spec))
                {
                    if (_logger.IsDebug) _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - invalid transaction ({txs[i].Hash})");
                    return false;
                }
            }

            if (spec.MaximumUncleCount < block.Ommers.Length)
            {
                _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - uncle count is {block.Ommers.Length} (MAX: {spec.MaximumUncleCount})");
                return false;
            }

            if (block.Header.OmmersHash != OmmersHash.Calculate(block))
            {
                _logger.Debug($"Invalid block ({block.ToString(Block.Format.FullHashAndNumber)}) - invalid uncles hash");
                return false;
            }
            
            if (!_ommersValidator.Validate(block.Header, block.Ommers))
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

            Keccak txRoot = new TxTrie(block.Transactions).RootHash;
            if (txRoot != block.Header.TxRoot)
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
                
                for (int i = 0; i < processedBlock.Transactions.Length; i++)
                {
                    if (receipts[i].Error != null && receipts[i].GasUsed == 0 && receipts[i].Error == "invalid")
                    {
                        if(_logger.IsError) _logger.Error($"  invalid transaction {i}");
                    }
                }
            }

            return isValid;
        }
    }
}
