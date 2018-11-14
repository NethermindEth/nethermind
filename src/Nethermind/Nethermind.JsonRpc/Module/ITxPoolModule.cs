using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public interface ITxPoolModule : IModule
    {
        ResultWrapper<TransactionPoolStatus> txpool_status();
        ResultWrapper<TransactionPoolContent> txpool_content();
        ResultWrapper<TransactionPoolInspection> txpool_inspect();
    }
}