using Nevermind.Core.Crypto;

namespace Nevermind.Blockchain.Validators
{
    public interface ISignatureValidator
    {
        bool Validate(Signature signature);
    }
}