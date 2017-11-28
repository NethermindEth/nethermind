using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain.Validators
{
    public class BlockValidator : IBlockValidator
    {
        private readonly BlockHeaderValidator _blockHeaderValidator;

        private readonly OmmersValidator _ommersValidator;
        private readonly ILogger _logger;

        public BlockValidator(BlockHeaderValidator blockHeaderValidator, OmmersValidator ommersValidator, ILogger logger)
        {
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
                if (!TransactionValidator.IsWellFormed(transaction))
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