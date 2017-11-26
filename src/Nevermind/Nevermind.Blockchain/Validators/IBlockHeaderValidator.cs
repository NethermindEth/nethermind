using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public interface IBlockHeaderValidator
    {
        bool Validate(BlockHeader blockHeader);
    }
}