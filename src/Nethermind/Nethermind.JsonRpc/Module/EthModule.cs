/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Model;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.JsonRpc.DataModel;
using Nethermind.KeyStore;
using Nethermind.Store;
using Block = Nethermind.JsonRpc.DataModel.Block;
using Transaction = Nethermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nethermind.JsonRpc.DataModel.TransactionReceipt;

namespace Nethermind.JsonRpc.Module
{
    public class EthModule : ModuleBase, IEthModule
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IBlockchainProcessor _blockchainProcessor;
        private readonly IBlockStore _blockStore;
        private readonly ITransactionStore _transactionStore;
        private readonly IDb _db;
        private readonly IStateProvider _stateProvider;
        private readonly IKeyStore _keyStore;
        private readonly IJsonRpcModelMapper _modelMapper;
        private readonly IReleaseSpec _releaseSpec;

        public EthModule(ILogger logger, IJsonSerializer jsonSerializer, IBlockchainProcessor blockchainProcessor, IStateProvider stateProvider, IKeyStore keyStore, IConfigurationProvider configurationProvider, IBlockStore blockStore, IDb db, IJsonRpcModelMapper modelMapper, IReleaseSpec releaseSpec, ITransactionStore transactionStore) : base(logger, configurationProvider)
        {
            _jsonSerializer = jsonSerializer;
            _blockchainProcessor = blockchainProcessor;
            _stateProvider = stateProvider;
            _keyStore = keyStore;
            _blockStore = blockStore;
            _db = db;
            _modelMapper = modelMapper;
            _releaseSpec = releaseSpec;
            _transactionStore = transactionStore;
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            //TODO implement properly
            return ResultWrapper<string>.Success("1");
            // TODO: this was inccorrect anyway
//           throw new NotImplementedException();
//            var version = EthereumNetwork.Main.GetNetworkId().ToString();
//            Logger.Debug($"eth_protocolVersion request, result: {version}");
//            return ResultWrapper<string>.Success(version);
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
            return ResultWrapper<Quantity>.Success(new Quantity(1));
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

        public ResultWrapper<Quantity> eth_getBalance(Data address, BlockParameter blockParameter)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountBalance(new Address(address.Value), blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }
            
            Logger.Debug($"eth_getBalance request {address.ToJson()}, {blockParameter}, result: {result.Data.GetValue()}");
            return result;
        }

        public ResultWrapper<Data> eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Quantity> eth_getTransactionCount(Data address, BlockParameter blockParameter)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountNonce(new Address(address.Value), blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            Logger.Debug($"eth_getTransactionCount request {address.ToJson()}, {blockParameter}, result: {result.Data.GetValue()}");
            return result;
        }

        public ResultWrapper<Quantity> eth_getBlockTransactionCountByHash(Data blockHash)
        {
            var block = _blockStore.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Quantity>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            Logger.Debug($"eth_getBlockTransactionCountByHash request {blockHash.ToJson()}, result: {block.Transactions.Count}");
            return ResultWrapper<Quantity>.Success(new Quantity(block.Transactions.Count));
        }

        public ResultWrapper<Quantity> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var transactionCount = GetTransactionCount(blockParameter);
            if (transactionCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(transactionCount.Result.Error, transactionCount.ErrorType);
            }

            Logger.Debug($"eth_getBlockTransactionCountByNumber request {blockParameter}, result: {transactionCount.Data.GetValue()}");
            return transactionCount;
        }

        public ResultWrapper<Quantity> eth_getUncleCountByBlockHash(Data blockHash)
        {
            var block = _blockStore.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Quantity>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            Logger.Debug($"eth_getUncleCountByBlockHash request {blockHash.ToJson()}, result: {block.Transactions.Count}");
            return ResultWrapper<Quantity>.Success(new Quantity(block.Ommers.Length));
        }

        public ResultWrapper<Quantity> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var ommersCount = GetOmmersCount(blockParameter);
            if (ommersCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(ommersCount.Result.Error, ommersCount.ErrorType);
            }

            Logger.Debug($"eth_getUncleCountByBlockNumber request {blockParameter}, result: {ommersCount.Data.GetValue()}");
            return ommersCount;
        }

        public ResultWrapper<Data> eth_getCode(Data address, BlockParameter blockParameter)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Data>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountCode(new Address(address.Value), blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            Logger.Debug($"eth_getCode request {address.ToJson()}, {blockParameter}, result: {result.Data.ToJson()}");
            return result;
        }

        public ResultWrapper<Data> eth_sign(Data address, Data message)
        {
            //TODO check how to deal with password
            SecureString secureString = new SecureString();
            secureString.AppendChar('?');
            
            var privateKey = _keyStore.GetKey(new Address(address.Value), secureString);
            if (privateKey.Item2.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Data>.Fail("Incorrect address");
            }

            var messageText = ConfigurationProvider.MessageEncoding.GetString(message.Value);
            var signatureText = string.Format(ConfigurationProvider.SignatureTemplate, messageText.Length, messageText);
            //TODO how to select proper chainId
            var signer = new EthereumSigner(_releaseSpec, ChainId.DefaultGethPrivateChain);
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
            var block = _blockStore.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(block, returnFullTransactionObjects);

            Logger.Debug($"eth_getBlockByHash request {blockHash.ToJson()}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Block> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Block>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetBlock(blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Block>.Fail(result.Result.Error, result.ErrorType);
            }

            var blockModel = _modelMapper.MapBlock(result.Data, returnFullTransactionObjects);

            Logger.Debug($"eth_getBlockByNumber request {blockParameter}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByHash(Data transactionHash)
        {
            var transaction = _transactionStore.GetTransaction(new Keccak(transactionHash.Value));
            if (transaction == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find transaction for hash: {transactionHash.Value}", ErrorType.NotFound);
            }
            var blockHash = _transactionStore.GetBlockHash(new Keccak(transactionHash.Value));
            if (blockHash == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find block hash for transaction: {transactionHash.Value}", ErrorType.NotFound);
            }
            var block = _blockStore.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            var transactionModel = _modelMapper.MapTransaction(transaction, block);
            Logger.Debug($"eth_getTransactionByHash request {transactionHash.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            var block = _blockStore.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }
            var index = positionIndex.GetValue();
            if (!index.HasValue)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is required", ErrorType.InvalidParams);
            }
            if (index.Value < 0 || index.Value > block.Transactions.Count - 1)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var transaction = block.Transactions[(int)index.Value];
            var transactionModel = _modelMapper.MapTransaction(transaction, block);

            Logger.Debug($"eth_getTransactionByBlockHashAndIndex request {blockHash.ToJson()}, index: {positionIndex.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Transaction>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetBlock(blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Transaction>.Fail(result.Result.Error, result.ErrorType);
            }

            var index = positionIndex.GetValue();
            if (!index.HasValue)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is required", ErrorType.InvalidParams);
            }
            if (index.Value < 0 || index.Value > result.Data.Transactions.Count - 1)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var transaction = result.Data.Transactions[(int)index.Value];
            var transactionModel = _modelMapper.MapTransaction(transaction, result.Data);

            Logger.Debug($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<TransactionReceipt> eth_getTransactionReceipt(Data transactionHash)
        {
            var transactionReceipt = _transactionStore.GetTransactionReceipt(new Keccak(transactionHash.Value));
            if (transactionReceipt == null)
            {
                return ResultWrapper<TransactionReceipt>.Fail($"Cannot find transactionReceipt for transaction hash: {transactionHash.Value}", ErrorType.NotFound);
            }
            var transaction = _transactionStore.GetTransaction(new Keccak(transactionHash.Value));
            if (transaction == null)
            {
                return ResultWrapper<TransactionReceipt>.Fail($"Cannot find transaction for hash: {transactionHash.Value}", ErrorType.NotFound);
            }
            var blockHash = _transactionStore.GetBlockHash(new Keccak(transactionHash.Value));
            if (blockHash == null)
            {
                return ResultWrapper<TransactionReceipt>.Fail($"Cannot find block hash for transaction: {transactionHash.Value}", ErrorType.NotFound);
            }
            var block = _blockStore.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<TransactionReceipt>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            var transactionReceiptModel = _modelMapper.MapTransactionReceipt(transactionReceipt, transaction, block);
            Logger.Debug($"eth_getTransactionReceipt request {transactionHash.ToJson()}, result: {GetJsonLog(transactionReceiptModel.ToJson())}");
            return ResultWrapper<TransactionReceipt>.Success(transactionReceiptModel);
        }

        public ResultWrapper<Block> eth_getUncleByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            var block = _blockStore.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }
            var index = positionIndex.GetValue();
            if (!index.HasValue)
            {
                return ResultWrapper<Block>.Fail("Position Index is required", ErrorType.InvalidParams);
            }
            if (index.Value < 0 || index.Value > block.Ommers.Length - 1)
            {
                return ResultWrapper<Block>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = block.Ommers[(int)index.Value];
            var ommer = _blockStore.FindBlock(ommerHeader.Hash, false);
            if (ommer == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }
            var blockModel = _modelMapper.MapBlock(ommer, false);

            Logger.Debug($"eth_getUncleByBlockHashAndIndex request {blockHash.ToJson()}, index: {positionIndex.ToJson()}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Block> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            if (_blockchainProcessor.HeadBlock?.Header == null)
            {
                return ResultWrapper<Block>.Fail($"Incorrect head block: {(_blockchainProcessor.HeadBlock != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetBlock(blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Block>.Fail(result.Result.Error, result.ErrorType);
            }

            var index = positionIndex.GetValue();
            if (!index.HasValue)
            {
                return ResultWrapper<Block>.Fail("Position Index is required", ErrorType.InvalidParams);
            }
            if (index.Value < 0 || index.Value > result.Data.Ommers.Length - 1)
            {
                return ResultWrapper<Block>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = result.Data.Ommers[(int)index.Value];
            var ommer = _blockStore.FindBlock(ommerHeader.Hash, false);
            if (ommer == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }
            var blockModel = _modelMapper.MapBlock(ommer, false);

            Logger.Debug($"eth_getUncleByBlockNumberAndIndex request {blockParameter}, index: {positionIndex.ToJson()}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
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

        private ResultWrapper<Quantity> GetOmmersCount(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var count = _blockchainProcessor.SuggestedBlock.Ommers.Length;
                return ResultWrapper<Quantity>.Success(new Quantity(count));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }
            return ResultWrapper<Quantity>.Success(new Quantity(block.Data.Ommers.Length));
        }

        private ResultWrapper<Quantity> GetTransactionCount(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var count = _blockchainProcessor.SuggestedBlock.Transactions.Count;
                return ResultWrapper<Quantity>.Success(new Quantity(count));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }
            return ResultWrapper<Quantity>.Success(new Quantity(block.Data.Transactions.Count));
        }

        private ResultWrapper<Data> GetAccountCode(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var code = _stateProvider.GetCode(address);
                return ResultWrapper<Data>.Success(new Data(code));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Data>.Fail(block.Result.Error);
            }
            return GetAccountCode(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<Quantity> GetAccountNonce(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var nonce = _stateProvider.GetNonce(address);
                return ResultWrapper<Quantity>.Success(new Quantity(nonce));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }
            return GetAccountNonce(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<Quantity> GetAccountBalance(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var balance = _stateProvider.GetBalance(address);
                return ResultWrapper<Quantity>.Success(new Quantity(balance));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }
            return GetAccountBalance(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<Core.Block> GetBlock(BlockParameter blockParameter)
        {
            switch (blockParameter.Type)
            {
                case BlockParameterType.Pending:
                    return ResultWrapper<Core.Block>.Success(_blockchainProcessor.SuggestedBlock);
                case BlockParameterType.Latest:
                    return ResultWrapper<Core.Block>.Success(_blockchainProcessor.HeadBlock);
                case BlockParameterType.Earliest:
                    var genesis = GetGenesisBlock(_blockchainProcessor.HeadBlock);
                    return ResultWrapper<Core.Block>.Success(genesis);
                case BlockParameterType.BlockId:
                    if (blockParameter.BlockId?.Value == null)
                    {
                        return ResultWrapper<Core.Block>.Fail($"Block id is required for {BlockParameterType.BlockId}", ErrorType.InvalidParams);
                    }
                    var value = blockParameter.BlockId.GetValue();
                    if (!value.HasValue)
                    {
                        return ResultWrapper<Core.Block>.Fail("Invalid block id", ErrorType.InvalidParams);
                    }
                    var block = _blockStore.FindBlock(value.Value);
                    if (block == null)
                    {
                        return ResultWrapper<Core.Block>.Fail($"Cannot find block for {value.Value}", ErrorType.NotFound);
                    }
                    return ResultWrapper<Core.Block>.Success(block);
                default:
                    throw new Exception($"BlockParameterType not supported: {blockParameter.Type}");
            }
        }

        private ResultWrapper<Quantity> GetAccountBalance(Address address, Keccak stateRoot)
        {
            var account = GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<Quantity>.Fail("Cannot find account", ErrorType.NotFound);
            }
            return ResultWrapper<Quantity>.Success(new Quantity(account.Balance));
        }

        private ResultWrapper<Quantity> GetAccountNonce(Address address, Keccak stateRoot)
        {
            var account = GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<Quantity>.Fail("Cannot find account", ErrorType.NotFound);
            }
            return ResultWrapper<Quantity>.Success(new Quantity(account.Nonce));
        }

        private ResultWrapper<Data> GetAccountCode(Address address, Keccak stateRoot)
        {
            var account = GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<Data>.Fail("Cannot find account", ErrorType.NotFound);
            }
            //TODO confirm it is correct
            var code = _stateProvider.GetCode(account.CodeHash);
            return ResultWrapper<Data>.Success(new Data(code));
        }

        private Account GetAccount(Address address, Keccak stateRoot)
        {
            var stateTree = new StateTree(stateRoot, _db);
            var rlp = stateTree.Get(address);
            if (rlp == null)
            {
                return null;
            }
            return Rlp.Decode<Account>(rlp);
        }

        private Core.Block GetGenesisBlock(Core.Block headBlock)
        {
            Core.Block parent;
            while ((parent = _blockStore.FindParent(headBlock)) != null)
            {
                headBlock = parent;
            }
            return headBlock;
        }

        private string GetJsonLog(object model)
        {
            return _jsonSerializer.Serialize(model);
        }
    }
}