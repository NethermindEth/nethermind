using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public interface IOmmersValidator
    {
        bool Validate(BlockHeader header, BlockHeader[] ommers);
    }
}