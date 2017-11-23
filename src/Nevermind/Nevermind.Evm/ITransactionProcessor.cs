using Nevermind.Core;

namespace Nevermind.Evm
{
    public interface ITransactionProcessor
    {
        TransactionReceipt Execute(Transaction transaction, BlockHeader block);
    }
}