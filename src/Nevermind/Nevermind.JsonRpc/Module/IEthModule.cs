using System.Collections.Generic;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface IEthModule : IModule
    {
        string eth_protocolVersion();
        SynchingResult eth_syncing();
        Data eth_coinbase();
        bool eth_mining();
        Quantity eth_hashrate();
        Quantity eth_gasPrice();
        IEnumerable<Data> eth_accounts();
        Quantity eth_blockNumber();
        Quantity eth_getBalance(Data data, BlockParameter blockParameter);
        Data eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter);
        Quantity eth_getTransactionCount(Data address, BlockParameter blockParameter);
        Quantity eth_getBlockTransactionCountByHash(Data blockHash);
        Quantity eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        Quantity eth_getUncleCountByBlockHash(Data blockHash);
        Quantity eth_getUncleCountByBlockNumber(BlockParameter blockParameter);
        Data eth_getCode(Data address, BlockParameter blockParameter);
        Data eth_sign(Data address, Data message);
        Data eth_sendTransaction(Transaction transaction);
        Data eth_sendRawTransaction(Data transation);
        Data eth_call(Transaction transactionCall, BlockParameter blockParameter);
        Quantity eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter);
        Block eth_getBlockByHash(Data blockHash, bool returnFullTransactionObjects);
        Block eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects);
        Transaction eth_getTransactionByHash(Data transactionHash);
        Transaction eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex);
        Transaction eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex);
        TransactionReceipt eth_getTransactionReceipt(Data transactionHash);
        Block eth_getUncleByBlockHashAndIndex(Data blockHash, Quantity positionIndex);
        Block eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex);
        IEnumerable<string> eth_getCompilers();
        Data eth_compileLLL(string code);
        Data eth_compileSolidity(string code);
        Data eth_compileSerpent(string code);
        Quantity eth_newFilter(Filter filter);
        Quantity eth_newBlockFilter(Filter filter);
        Quantity eth_newPendingTransactionFilter(Filter filter);
        bool eth_uninstallFilter(Quantity filterId);
        IEnumerable<Log> eth_getFilterChanges(Quantity filterId);
        IEnumerable<Log> eth_getFilterLogs(Quantity filterId);
        IEnumerable<Log> eth_getLogs(Filter filter);
        IEnumerable<Data> eth_getWork();
        bool eth_submitWork(IEnumerable<Data> data);
        bool eth_submitHashrate(string hashRate, string id);
    }
}