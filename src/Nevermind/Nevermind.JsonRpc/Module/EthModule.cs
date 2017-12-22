using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nevermind.Blockchain;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Core.Potocol;
using Nevermind.Json;
using Nevermind.JsonRpc.DataModel;
using Nevermind.KeyStore;
using Nevermind.Store;
using Nevermind.Utils.Model;
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
        private readonly IKeyStore _keyStore;

        public EthModule(ILogger logger, IJsonSerializer jsonSerializer, IBlockchainProcessor blockchainProcessor, IStateProvider stateProvider, IKeyStore keyStore, IConfigurationProvider configurationProvider) : base(logger, configurationProvider)
        {
            _jsonSerializer = jsonSerializer;
            _blockchainProcessor = blockchainProcessor;
            _stateProvider = stateProvider;
            _keyStore = keyStore;
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            var version = ((int) ProtocolVersion.EthereumMainnet).ToString();
            Logger.Debug($"eth_protocolVersion request, result: {version}");
            return ResultWrapper<string>.Success(version);
        }

        public ResultWrapper<SynchingResult> eth_syncing()
        {
            var result = new SynchingResult {IsSynching = false};
            Logger.Debug($"eth_syncing request, result: {result.ToJson()}");
            return ResultWrapper<SynchingResult>.Success(result);
        }

        public ResultWrapper<Data> eth_coinbase()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<bool> eth_mining()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_hashrate()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_gasPrice()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<IEnumerable<Data>> eth_accounts()
        {
            var result = _keyStore.GetKeyAddresses();
            if (result.Item2.ResultType == ResultType.Failure)
            {
                return  ResultWrapper<IEnumerable<Data>>.Fail($"Error while getting key addresses from keystore: {result.Item2.Error}");
            }
            var data = result.Item1.Select(x => new Data(x.Hex)).ToArray();
            Logger.Debug($"eth_accounts request, result: {string.Join(", ", data.Select(x => x.Value.ToString()))}");
            return ResultWrapper<IEnumerable<Data>>.Success(data);
        }

        public ResultWrapper<Quantity> eth_blockNumber()
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }
            var number = _blockchainProcessor.HeadBlock.Header.Number;
            Logger.Debug($"eth_blockNumber request, result: {number}");
            return ResultWrapper<Quantity>.Success(new Quantity(number));
        }

        public ResultWrapper<Quantity> eth_getBalance(Data data, BlockParameter blockParameter)
        {
            //TODO support other options
            var address = new Address(data.Value);
            var balance = _stateProvider.GetBalance(address);
            Logger.Debug($"eth_getBalance request {data.ToJson()}, {blockParameter}, result: {balance}");
            return ResultWrapper<Quantity>.Success(new Quantity(balance));
        }

        public ResultWrapper<Data> eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_getTransactionCount(Data address, BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_getBlockTransactionCountByHash(Data blockHash)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_getUncleCountByBlockHash(Data blockHash)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_getCode(Data address, BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_sign(Data address, Data message)
        {
            //TODO check how to deal with password
            var privateKey = _keyStore.GetKey(new Address(address.Value), string.Empty);
            if (privateKey.Item2.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Data>.Fail("Incorrect address");
            }

            var messageText = ConfigurationProvider.MessageEncoding.GetString(message.Value.ToBytes());
            var signatureText = string.Format(ConfigurationProvider.SignatureTemplate, messageText.Length, messageText);
            //TODO how to select proper protocol, chainId
            var signer = new Signer(new FrontierProtocolSpecification(), ChainId.DefaultGethPrivateChain);
            var signature = signer.Sign(privateKey.Item1, Keccak.Compute(signatureText));
            Logger.Debug($"eth_sign request {address.ToJson()}, {message.ToJson()}, result: {signature}");
            return ResultWrapper<Data>.Success(new Data(signature.Bytes));
        }

        public ResultWrapper<Data> eth_sendTransaction(Transaction transaction)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_sendRawTransaction(Data transation)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_call(Transaction transactionCall, BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Block> eth_getBlockByHash(Data blockHash, bool returnFullTransactionObjects)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Block> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Transaction> eth_getTransactionByHash(Data transactionHash)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<TransactionReceipt> eth_getTransactionReceipt(Data transactionHash)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Block> eth_getUncleByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Block> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<IEnumerable<string>> eth_getCompilers()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_compileLLL(string code)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_compileSolidity(string code)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_compileSerpent(string code)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_newFilter(Filter filter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_newBlockFilter(Filter filter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_newPendingTransactionFilter(Filter filter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<bool> eth_uninstallFilter(Quantity filterId)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<IEnumerable<Log>> eth_getFilterChanges(Quantity filterId)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<IEnumerable<Log>> eth_getFilterLogs(Quantity filterId)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<IEnumerable<Log>> eth_getLogs(Filter filter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<IEnumerable<Data>> eth_getWork()
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<bool> eth_submitWork(Data nonce, Data headerPowHash, Data mixDigest)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<bool> eth_submitHashrate(string hashRate, string id)
        {
            throw new NotImplementedException();
        }
    }
}