using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public interface IBlockValidator
    {
        bool Validate(Block block);
    }
}