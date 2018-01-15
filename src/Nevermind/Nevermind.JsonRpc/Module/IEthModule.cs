using System.Collections.Generic;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public interface IEthModule : IModule
    {
        ResultWrapper<string> eth_protocolVersion();
        ResultWrapper<SynchingResult> eth_syncing();
        ResultWrapper<Data> eth_coinbase();
        ResultWrapper<bool> eth_mining();
        ResultWrapper<Quantity> eth_hashrate();
        ResultWrapper<Quantity> eth_gasPrice();
        ResultWrapper<IEnumerable<Data>> eth_accounts();
        ResultWrapper<Quantity> eth_blockNumber();
        ResultWrapper<Quantity> eth_getBalance(Data address, BlockParameter blockParameter);
        ResultWrapper<Data> eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_getTransactionCount(Data address, BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_getBlockTransactionCountByHash(Data blockHash);
        ResultWrapper<Quantity> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_getUncleCountByBlockHash(Data blockHash);
        ResultWrapper<Quantity> eth_getUncleCountByBlockNumber(BlockParameter blockParameter);
        ResultWrapper<Data> eth_getCode(Data address, BlockParameter blockParameter);
        ResultWrapper<Data> eth_sign(Data address, Data message);
        ResultWrapper<Data> eth_sendTransaction(Transaction transaction);
        ResultWrapper<Data> eth_sendRawTransaction(Data transation);
        ResultWrapper<Data> eth_call(Transaction transactionCall, BlockParameter blockParameter);
        ResultWrapper<Quantity> eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter);
        ResultWrapper<Block> eth_getBlockByHash(Data blockHash, bool returnFullTransactionObjects);
        ResultWrapper<Block> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects);
        ResultWrapper<Transaction> eth_getTransactionByHash(Data transactionHash);
        ResultWrapper<Transaction> eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex);
        ResultWrapper<Transaction> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex);
        ResultWrapper<TransactionReceipt> eth_getTransactionReceipt(Data transactionHash);
        ResultWrapper<Block> eth_getUncleByBlockHashAndIndex(Data blockHash, Quantity positionIndex);
        ResultWrapper<Block> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex);
        ResultWrapper<IEnumerable<string>> eth_getCompilers();
        ResultWrapper<Data> eth_compileLLL(string code);
        ResultWrapper<Data> eth_compileSolidity(string code);
        ResultWrapper<Data> eth_compileSerpent(string code);
        ResultWrapper<Quantity> eth_newFilter(Filter filter);
        ResultWrapper<Quantity> eth_newBlockFilter(Filter filter);
        ResultWrapper<Quantity> eth_newPendingTransactionFilter(Filter filter);
        ResultWrapper<bool> eth_uninstallFilter(Quantity filterId);
        ResultWrapper<IEnumerable<Log>> eth_getFilterChanges(Quantity filterId);
        ResultWrapper<IEnumerable<Log>> eth_getFilterLogs(Quantity filterId);
        ResultWrapper<IEnumerable<Log>> eth_getLogs(Filter filter);
        ResultWrapper<IEnumerable<Data>> eth_getWork();
        ResultWrapper<bool> eth_submitWork(Data nonce, Data headerPowHash, Data mixDigest);
        ResultWrapper<bool> eth_submitHashrate(string hashRate, string id);
    }
}