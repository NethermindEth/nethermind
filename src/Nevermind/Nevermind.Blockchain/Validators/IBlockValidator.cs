using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public interface IBlockValidator
    {
        bool ValidateSuggestedBlock(Block suggestedBlock);
        bool ValidateProcessedBlock(Block processedBlock, Block suggestedBlock);
    }
}