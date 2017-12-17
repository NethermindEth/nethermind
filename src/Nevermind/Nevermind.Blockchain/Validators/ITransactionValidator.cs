using Nevermind.Core;

namespace Nevermind.Blockchain.Validators
{
    public interface ITransactionValidator
    {
        bool IsWellFormed(Transaction transaction, bool ignoreSignature = false);
    }
}