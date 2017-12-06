using System.Collections.Generic;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class EthModule : IEthModule
    {
        public string eth_protocolVersion()
        {
            throw new System.NotImplementedException();
        }

        public SynchingResult eth_syncing()
        {
            throw new System.NotImplementedException();
        }

        public Data eth_coinbase()
        {
            throw new System.NotImplementedException();
        }

        public bool eth_mining()
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_hashrate()
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_gasPrice()
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<Data> eth_accounts()
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_blockNumber()
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_getBalance(Data data, BlockParameter blockParameter)
        {
            return new Quantity {Value = 1000};
        }

        public Data eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_getTransactionCount(Data address, BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_getBlockTransactionCountByHash(Data blockHash)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_getUncleCountByBlockHash(Data blockHash)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_getCode(Data address, BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_sign(Data address, Data message)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_sendTransaction(Transaction transaction)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_sendRawTransaction(Data transation)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_call(Transaction transactionCall, BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter)
        {
            throw new System.NotImplementedException();
        }

        public Block eth_getBlockByHash(Data blockHash, bool returnFullTransactionObjects)
        {
            throw new System.NotImplementedException();
        }

        public Block eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            throw new System.NotImplementedException();
        }

        public Transaction eth_getTransactionByHash(Data transactionHash)
        {
            throw new System.NotImplementedException();
        }

        public Transaction eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            throw new System.NotImplementedException();
        }

        public Transaction eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            throw new System.NotImplementedException();
        }

        public TransactionReceipt eth_getTransactionReceipt(Data transactionHash)
        {
            throw new System.NotImplementedException();
        }

        public Block eth_getUncleByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            throw new System.NotImplementedException();
        }

        public Block eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<string> eth_getCompilers()
        {
            throw new System.NotImplementedException();
        }

        public Data eth_compileLLL(string code)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_compileSolidity(string code)
        {
            throw new System.NotImplementedException();
        }

        public Data eth_compileSerpent(string code)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_newFilter(Filter filter)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_newBlockFilter(Filter filter)
        {
            throw new System.NotImplementedException();
        }

        public Quantity eth_newPendingTransactionFilter(Filter filter)
        {
            throw new System.NotImplementedException();
        }

        public bool eth_uninstallFilter(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<Log> eth_getFilterChanges(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<Log> eth_getFilterLogs(Quantity filterId)
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<Log> eth_getLogs(Filter filter)
        {
            throw new System.NotImplementedException();
        }

        public IEnumerable<Data> eth_getWork()
        {
            throw new System.NotImplementedException();
        }

        public bool eth_submitWork(IEnumerable<Data> data)
        {
            throw new System.NotImplementedException();
        }

        public bool eth_submitHashrate(string hashRate, string id)
        {
            throw new System.NotImplementedException();
        }

        public void Initialize()
        {
            throw new System.NotImplementedException();
        }
    }
}