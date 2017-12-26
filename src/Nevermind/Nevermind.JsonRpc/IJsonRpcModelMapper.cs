using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc
{
    public interface IJsonRpcModelMapper
    {
        Block MapBlock(Core.Block block, bool returnFullTransactionObjects);
        Transaction MapTransaction(Core.Transaction transaction);
        TransactionReceipt MapTransactionReceipt(Core.TransactionReceipt transaction);
        Log MapLog();
    }
}