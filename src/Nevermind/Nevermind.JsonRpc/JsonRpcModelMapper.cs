using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc
{
    public class JsonRpcModelMapper : IJsonRpcModelMapper
    {
        public Block MapBlock(Core.Block block, bool returnFullTransactionObjects)
        {
            throw new System.NotImplementedException();
        }

        public Transaction MapTransaction(Core.Transaction transaction)
        {
            throw new System.NotImplementedException();
        }

        public TransactionReceipt MapTransactionReceipt(Core.TransactionReceipt transaction)
        {
            throw new System.NotImplementedException();
        }

        public Log MapLog()
        {
            throw new System.NotImplementedException();
        }
    }
}