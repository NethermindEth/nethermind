using System;

namespace Nevermind.Core.Validators
{
    public class BlockValidator
    {
        public bool Finalize(Block block)
        {
            throw new NotImplementedException();
        }

        public bool IsValid(Block block)
        {
            if (!OmmersValidator.ValidateOmmers(block))
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

            return BlockHeaderValidator.IsValid(block.Header);
        }
    }
}