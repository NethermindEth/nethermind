using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public class BlockValidator
    {
        private readonly BlockHeaderValidator _blockHeaderValidator;

        private readonly OmmersValidator _ommersValidator;

        public BlockValidator(BlockHeaderValidator blockHeaderValidator, OmmersValidator ommersValidator)
        {
            _ommersValidator = ommersValidator;
            _blockHeaderValidator = blockHeaderValidator;
        }

        public bool IsValid(Block block)
        {
            if (!_ommersValidator.ValidateOmmers(block))
            {
                return false;
            }

            foreach (Transaction transaction in block.Transactions)
            {
                if (!TransactionValidator.IsWellFormed(transaction))
                {
                    return false;
                }
            }

            return _blockHeaderValidator.IsValid(block.Header);
        }
    }
}