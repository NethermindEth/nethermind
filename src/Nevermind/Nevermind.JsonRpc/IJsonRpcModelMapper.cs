using Nevermind.Core;
using Nevermind.JsonRpc.DataModel;
using Block = Nevermind.JsonRpc.DataModel.Block;
using Transaction = Nevermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nevermind.JsonRpc.DataModel.TransactionReceipt;

namespace Nevermind.JsonRpc
{
    public interface IJsonRpcModelMapper
    {
        Block MapBlock(Core.Block block, bool returnFullTransactionObjects);
        Transaction MapTransaction(Core.Transaction transaction, Core.Block block);
        TransactionReceipt MapTransactionReceipt(Core.TransactionReceipt receipt, Core.Transaction transaction, Core.Block block);
        Log MapLog(LogEntry logEntry);
    }
}