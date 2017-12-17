using System;
using System.Collections.Generic;
using System.Text;
using Nevermind.Blockchain;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Potocol;
using Nevermind.Json;
using Nevermind.JsonRpc.DataModel;
using Nevermind.Store;
using Block = Nevermind.JsonRpc.DataModel.Block;
using Transaction = Nevermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nevermind.JsonRpc.DataModel.TransactionReceipt;

namespace Nevermind.JsonRpc.Module
{
    public class EthModule : ModuleBase, IEthModule
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IStateProvider _stateProvider;

        public EthModule(ILogger logger, IJsonSerializer jsonSerializer, IBlockchainProcessor blockchainProcessor, IStateProvider stateProvider) : base(logger)
        {
            _jsonSerializer = jsonSerializer;
            _blockchainProcessor = blockchainProcessor;
            _stateProvider = stateProvider;
        }

        public string eth_protocolVersion()
        {
            return ((int)ProtocolVersion.EthereumMainnet).ToString();
        }

        public SynchingResult eth_syncing()
        {
            return new SynchingResult(_jsonSerializer) {IsSynching = false};
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
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                Logger.Error($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
                throw new Exception("Incorrect head block");
            }
            var number = _blockchainProcessor.HeadBlock.Header.Number;
            Logger.Debug($"eth_blockNumber request, result: {number}");
            return new Quantity(number);
        }

        public Quantity eth_getBalance(Data data, BlockParameter blockParameter)
        {
            //TODO support other options
            var address = new Address(data.Value);
            var balance = _stateProvider.GetBalance(address);
            Logger.Debug($"eth_getBalance request {data.ToJson()}, {blockParameter}, result: {balance}");
            return new Quantity(balance);
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
            //get private ket for signing
            var code =_stateProvider.GetCode(new Address(address.Value));
            var privateKey = new PrivateKey(new Hex(code));

            var messageText = Encoding.UTF8.GetString(message.Value.ToBytes());
            var signatureText = "\x19Ethereum Signed Message:\n" + messageText.Length + messageText;
            var signer = new Signer(new FrontierProtocolSpecification(), ChainId.DefaultGethPrivateChain);
            var signature = signer.Sign(privateKey, Keccak.Compute(signatureText));
            return new Data(signature.Bytes);
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