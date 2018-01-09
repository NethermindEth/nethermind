using System.Linq;
using System.Numerics;
using Nevermind.Core;

namespace Nevermind.Evm
{
    public interface ITransactionProcessor
    {
        TransactionReceipt Execute(Transaction transaction, BlockHeader block);
    }
}