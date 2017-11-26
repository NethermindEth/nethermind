using Nevermind.Core;

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

        public bool Validate(Block block)
        {
            if (!_ommersValidator.Validate(block.Header, block.Ommers))
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

            return _blockHeaderValidator.Validate(block.Header);
        }
    }
}