using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;

namespace Nevermind.Blockchain.Validators
{
    public class BlockValidator : IBlockValidator
    {
        private readonly BlockHeaderValidator _blockHeaderValidator;

        private readonly OmmersValidator _ommersValidator;

        public BlockValidator(BlockHeaderValidator blockHeaderValidator, OmmersValidator ommersValidator)
        {
            _ommersValidator = ommersValidator;
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

            return _blockHeaderValidator.Validate(suggestedBlock.Header);
        }

        public bool ValidateProcessedBlock(Block processedBlock, Block suggestedBlock)
        {
            return processedBlock.Header.Hash == suggestedBlock.Header.Hash;
        }
    }
}