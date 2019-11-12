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
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Eip1186;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModule : IEthModule
    {
        private Encoding _messageEncoding = Encoding.UTF8;

        private readonly IBlockchainBridge _blockchainBridge;

        private readonly ILogger _logger;

        public EthModule(ILogManager logManager, IBlockchainBridge blockchainBridge)
        {
            _logger = logManager.GetClassLogger();
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            return ResultWrapper<string>.Success("62");
        }

        public ResultWrapper<SyncingResult> eth_syncing()
        {
            SyncingResult result;
            if (_blockchainBridge.IsSyncing)
            {
                result = new SyncingResult
                {
                    CurrentBlock = _blockchainBridge.Head.Number,
                    HighestBlock = _blockchainBridge.BestKnown,
                    StartingBlock = 0L,
                    IsSyncing = true
                };
            }
            else
            {
                result = SyncingResult.NotSyncing;
            }

            return ResultWrapper<SyncingResult>.Success(result);
        }

        public ResultWrapper<byte[]> eth_snapshot()
        {
            return ResultWrapper<byte[]>.Fail("eth_snapshot not supported");
        }

        public ResultWrapper<Address> eth_coinbase()
        {
            return ResultWrapper<Address>.Success(Address.Zero);
        }

        public ResultWrapper<bool?> eth_mining()
        {
            return ResultWrapper<bool?>.Success(false);
        }

        public ResultWrapper<UInt256?> eth_hashrate()
        {
            return ResultWrapper<UInt256?>.Success(0);
        }

        [Todo("Gas pricer to be implemented")]
        public ResultWrapper<UInt256?> eth_gasPrice()
        {
            return ResultWrapper<UInt256?>.Success(20.GWei());
        }

        public ResultWrapper<IEnumerable<Address>> eth_accounts()
        {
            try
            {
                var result = _blockchainBridge.GetWalletAccounts();
                Address[] data = result.ToArray();
                return ResultWrapper<IEnumerable<Address>>.Success(data.ToArray());
            }
            catch (Exception)
            {
                return ResultWrapper<IEnumerable<Address>>.Fail("Error while getting key addresses from wallet.");
            }
        }

        public Task<ResultWrapper<long?>> eth_blockNumber()
        {
            if (_blockchainBridge.Head == null)
            {
                return Task.FromResult(ResultWrapper<long?>.Fail($"Incorrect head block", ErrorType.InternalError, null));
            }

            var number = _blockchainBridge.Head.Number;
            return Task.FromResult(ResultWrapper<long?>.Success(number));
        }

        public Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter blockParameter = null)
        {
            if (_blockchainBridge.Head == null)
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail("Incorrect head block", ErrorType.InternalError, null));
            }

            var result = GetAccountBalance(address, blockParameter ?? BlockParameter.Latest);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail($"Could not find balance of {address} at {blockParameter}", ErrorType.InternalError, null));
            }

            return Task.FromResult(result);
        }

        public ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex, BlockParameter blockParameter = null)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<byte[]>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            return GetStorage(address, positionIndex, blockParameter ?? BlockParameter.Latest);
        }

        public ResultWrapper<UInt256?> eth_getTransactionCount(Address address, BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<UInt256?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
            }

            return GetAccountNonce(address, blockParameter ?? BlockParameter.Latest);
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            Block block = _blockchainBridge.FindBlock(blockHash);
            if (block == null)
            {
                return ResultWrapper<UInt256?>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound, null);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)block.Transactions.Length);
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<UInt256?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
            }

            var transactionCount = GetTransactionCount(blockParameter);
            if (transactionCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<UInt256?>.Fail(transactionCount.Result.Error, transactionCount.ErrorType, null);
            }

            if (_logger.IsTrace) _logger.Trace($"eth_getBlockTransactionCountByNumber request {blockParameter}, result: {transactionCount.Data}");
            return transactionCount;
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            var block = _blockchainBridge.FindBlock(blockHash);
            if (block == null)
            {
                return ResultWrapper<UInt256?>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound, null);
            }

            if (_logger.IsTrace) _logger.Trace($"eth_getUncleCountByBlockHash request {blockHash}, result: {block.Transactions.Length}");
            return ResultWrapper<UInt256?>.Success((UInt256)block.Ommers.Length);
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<UInt256?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
            }

            var ommersCount = GetOmmersCount(blockParameter);
            if (ommersCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<UInt256?>.Fail(ommersCount.Result.Error, ommersCount.ErrorType);
            }

            if (_logger.IsTrace) _logger.Trace($"eth_getUncleCountByBlockNumber request {blockParameter}, result: {ommersCount.Data}");
            return ommersCount;
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter = null)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<byte[]>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountCode(address, blockParameter ?? BlockParameter.Latest);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            if (_logger.IsTrace) _logger.Trace($"eth_getCode request {address}, {blockParameter}, result: {result.Data.ToHexString(true)}");
            return result;
        }


        public ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message)
        {
            Signature sig;
            try
            {
                Address address = addressData;
                string messageText = _messageEncoding.GetString(message);
                const string signatureTemplate = "\x19Ethereum Signed Message:\n{0}{1}";
                string signatureText = string.Format(signatureTemplate, messageText.Length, messageText);
                sig = _blockchainBridge.Sign(address, Keccak.Compute(signatureText));
            }
            catch (Exception)
            {
                return ResultWrapper<byte[]>.Fail($"Unable to sign as {addressData}");
            }

            if (_logger.IsTrace) _logger.Trace($"eth_sign request {addressData}, {message}, result: {sig}");
            return ResultWrapper<byte[]>.Success(sig.Bytes);
        }

        public ResultWrapper<Keccak> eth_sendTransaction(TransactionForRpc transactionForRpc)
        {
            Transaction tx = transactionForRpc.ToTransaction();
            if (tx.Signature == null)
            {
                tx.Nonce = _blockchainBridge.GetNonce(tx.SenderAddress);
                _blockchainBridge.Sign(tx);
            }

            Keccak txHash = _blockchainBridge.SendTransaction(tx, true);
            return ResultWrapper<Keccak>.Success(txHash);
        }

        public ResultWrapper<Keccak> eth_sendRawTransaction(byte[] transaction)
        {
            Transaction tx = Rlp.Decode<Transaction>(transaction);
            Keccak txHash = _blockchainBridge.SendTransaction(tx, true);
            return ResultWrapper<Keccak>.Success(txHash);
        }

        public ResultWrapper<byte[]> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null)
        {
            BlockHeader block = _blockchainBridge.GetHeader(blockParameter ?? BlockParameter.Latest);

            var tx = transactionCall.ToTransaction();
            tx.GasPrice = 0;
            if (tx.GasLimit < 21000)
            {
                tx.GasLimit = 10000000;    
            }

            if (tx.To == null)
            {
                return ResultWrapper<byte[]>.Fail($"Recipient address not specified on the transaction.", ErrorType.InvalidParams);
            }
            
            BlockchainBridge.CallOutput result = _blockchainBridge.Call(block, tx);

            if (result.Error != null)
            {
                return ResultWrapper<byte[]>.Fail($"VM Exception while processing transaction: {result.Error}", ErrorType.ExecutionError, result.OutputData);
            }

            return ResultWrapper<byte[]>.Success(result.OutputData);
        }

        public ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall)
        {
            Block headBlock = _blockchainBridge.FindHeadBlock();
            if (transactionCall.Gas == null)
            {
                transactionCall.Gas = headBlock.GasLimit;
            }

            long result = _blockchainBridge.EstimateGas(headBlock, transactionCall.ToTransaction());
            return ResultWrapper<UInt256?>.Success((UInt256)result);
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            var block = _blockchainBridge.FindBlock(blockHash);
            if (block != null && returnFullTransactionObjects)
            {
                _blockchainBridge.RecoverTxSenders(block);
            }

            return ResultWrapper<BlockForRpc>.Success(block == null ? null : new BlockForRpc(block, returnFullTransactionObjects));
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BlockForRpc>.Fail("Incorrect head block");
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter, true, true);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<BlockForRpc>.Fail(ex.Message, ex.ErrorType, null);
            }

            if (block != null && returnFullTransactionObjects)
            {
                _blockchainBridge.RecoverTxSenders(block);
            }

            return ResultWrapper<BlockForRpc>.Success(block == null ? null : new BlockForRpc(block, returnFullTransactionObjects));
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash)
        {
            (TxReceipt receipt, Transaction transaction) = _blockchainBridge.GetTransaction(transactionHash);
            if (transaction == null)
            {
                return ResultWrapper<TransactionForRpc>.Success(null);
            }

            _blockchainBridge.RecoverTxSender(transaction, receipt.BlockNumber);
            var transactionModel = new TransactionForRpc(receipt.BlockHash, receipt.BlockNumber, receipt.Index, transaction);
            if (_logger.IsTrace) _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex)
        {
            var block = _blockchainBridge.FindBlock(blockHash);
            if (block == null)
            {
                return ResultWrapper<TransactionForRpc>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var transaction = block.Transactions[(int) positionIndex];
            _blockchainBridge.RecoverTxSender(transaction, block.Number);

            var transactionModel = new TransactionForRpc(block.Hash, block.Number, (int) positionIndex, transaction);

            if (_logger.IsDebug) _logger.Debug($"eth_getTransactionByBlockHashAndIndex request {blockHash}, index: {positionIndex}, result: {transactionModel.Hash}");
            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<TransactionForRpc>.Fail($"Incorrect head block");
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<TransactionForRpc>.Fail(ex.Message, ex.ErrorType, null);
            }

            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var transaction = block.Transactions[(int) positionIndex];
            _blockchainBridge.RecoverTxSender(transaction, block.Number);

            var transactionModel = new TransactionForRpc(block.Hash, block.Number, (int) positionIndex, transaction);

            if (_logger.IsDebug) _logger.Debug($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {transactionModel.Hash}");
            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public ResultWrapper<ReceiptForRpc> eth_getTransactionReceipt(Keccak txHash)
        {
            var receipt = _blockchainBridge.GetReceipt(txHash);
            if (receipt == null)
            {
                return ResultWrapper<ReceiptForRpc>.Success(null);
            }

            var receiptModel = new ReceiptForRpc(txHash, receipt);
            if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
            return ResultWrapper<ReceiptForRpc>.Success(receiptModel);
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex)
        {
            Keccak blockHash = blockHashData;
            var block = _blockchainBridge.FindBlock(blockHash);
            if (block == null)
            {
                return ResultWrapper<BlockForRpc>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
            {
                return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = block.Ommers[(int) positionIndex];
            var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash);
            if (ommer == null)
            {
                return ResultWrapper<BlockForRpc>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"eth_getUncleByBlockHashAndIndex request {blockHashData}, index: {positionIndex}, result: {block}");
            return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(block, false));
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BlockForRpc>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<BlockForRpc>.Fail(ex.Message, ex.ErrorType, null);
            }

            if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
            {
                return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = block.Ommers[(int) positionIndex];
            var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash);
            if (ommer == null)
            {
                return ResultWrapper<BlockForRpc>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }

            _blockchainBridge.RecoverTxSenders(ommer);

            return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(block, false));
        }

        public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
        {
            FilterBlock fromBlock = filter.FromBlock.ToFilterBlock();
            FilterBlock toBlock = filter.ToBlock.ToFilterBlock();
            int filterId = _blockchainBridge.NewFilter(fromBlock, toBlock, filter.Address, filter.Topics);
            return ResultWrapper<UInt256?>.Success((UInt256)filterId);
        }
        
        public ResultWrapper<UInt256?> eth_newBlockFilter()
        {
            int filterId = _blockchainBridge.NewBlockFilter();
            return ResultWrapper<UInt256?>.Success((UInt256)filterId);
        }

        public ResultWrapper<UInt256?> eth_newPendingTransactionFilter()
        {
            int filterId = _blockchainBridge.NewPendingTransactionFilter();
            return ResultWrapper<UInt256?>.Success((UInt256)filterId);
        }

        public ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId)
        {
            _blockchainBridge.UninstallFilter((int) filterId);
            return ResultWrapper<bool?>.Success(true);
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId)
        {
            int id = (int) filterId;
            FilterType filterType = _blockchainBridge.GetFilterType(id);
            switch (filterType)
            {
                case FilterType.BlockFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetBlockFilterChanges(id)
                            .Select(b => new JsonRpc.Data.Data(b.Bytes)).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                case FilterType.PendingTransactionFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge
                            .GetPendingTransactionFilterChanges(id).Select(b => new JsonRpc.Data.Data(b.Bytes))
                            .ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                case FilterType.LogFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(
                            _blockchainBridge.GetLogFilterChanges(id).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                default:
                {
                    throw new NotSupportedException($"Filter type {filterType} is not supported");
                }
            }
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
        {
            var id = (int) filterId;

            return _blockchainBridge.FilterExists(id)
                ? ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetFilterLogs(id))
                : ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: '{filterId}' does not exist.");
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            FilterBlock fromBlock = filter.FromBlock.ToFilterBlock();
            FilterBlock toBlock = filter.ToBlock.ToFilterBlock();

            return ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetLogs(fromBlock, toBlock,
                filter.Address,
                filter.Topics));
        }

        public ResultWrapper<IEnumerable<byte[]>> eth_getWork()
        {
            return ResultWrapper<IEnumerable<byte[]>>.Fail("eth_getWork not supported", ErrorType.MethodNotFound);
        }

        public ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest)
        {
            return ResultWrapper<bool?>.Fail("eth_submitWork not supported", ErrorType.MethodNotFound, null);
        }

        public ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id)
        {
            return ResultWrapper<bool?>.Fail("eth_submitHashrate not supported", ErrorType.MethodNotFound, null);
        }
        
        // https://github.com/ethereum/EIPs/issues/1186	
        public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] storageKeys, BlockParameter blockParameter)	
        {	
            BlockHeader header;	
            try	
            {	
                header = _blockchainBridge.GetHeader(blockParameter);	
            }	
            catch (JsonRpcException ex)	
            {	
                return ResultWrapper<AccountProof>.Fail(ex.Message, ex.ErrorType, null);	
            }	

            ProofCollector proofCollector = new ProofCollector(accountAddress, storageKeys);	
            _blockchainBridge.RunTreeVisitor(proofCollector, header.StateRoot);	

            return ResultWrapper<AccountProof>.Success(proofCollector.BuildResult());	
        }	


        private ResultWrapper<UInt256?> GetOmmersCount(BlockParameter blockParameter)
        {
            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<UInt256?>.Fail(ex.Message, ex.ErrorType, null);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)block.Ommers.Length);
        }

        private ResultWrapper<UInt256?> GetTransactionCount(BlockParameter blockParameter = null)
        {
            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter ?? BlockParameter.Latest);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<UInt256?>.Fail(ex.Message, ex.ErrorType, null);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)block.Transactions.Length);
        }

        public ResultWrapper<long> eth_chainId()
        {
            try
            {
                long chainId = _blockchainBridge.GetChainId();
                return ResultWrapper<long>.Success(chainId);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<long>.Fail(ex.Message, ex.ErrorType, 0L);
            }
        }

        private ResultWrapper<byte[]> GetAccountCode(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            BlockHeader header;
            try
            {
                header = _blockchainBridge.GetHeader(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<byte[]>.Fail(ex.Message, ex.ErrorType, null);
            }

            return GetAccountCode(address, header.StateRoot);
        }

        private ResultWrapper<UInt256?> GetAccountNonce(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            BlockHeader header;
            try
            {
                header = _blockchainBridge.GetHeader(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<UInt256?>.Fail(ex.Message, ex.ErrorType, null);
            }

            if (header == null)
            {
                return ResultWrapper<UInt256?>.Fail("Block not found", ErrorType.NotFound, null);
            }

            Account account = _blockchainBridge.GetAccount(address, header.StateRoot);
            return ResultWrapper<UInt256?>.Success(account?.Nonce ?? 0);
        }

        private ResultWrapper<UInt256?> GetAccountBalance(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            BlockHeader header;
            try
            {
                header = _blockchainBridge.GetHeader(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<UInt256?>.Fail(ex.Message, ex.ErrorType, null);
            }

            return GetAccountBalance(address, header.StateRoot);
        }

        private ResultWrapper<byte[]> GetStorage(Address address, UInt256 index, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            BlockHeader header;
            try
            {
                header = _blockchainBridge.GetHeader(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<byte[]>.Fail(ex.Message, ex.ErrorType, null);
            }

            return GetAccountStorage(address, index, header.StateRoot);
        }

        private ResultWrapper<byte[]> GetAccountStorage(Address address, UInt256 index, Keccak stateRoot)
        {
            Account account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Bytes.Empty);
            }

            return ResultWrapper<byte[]>.Success(_blockchainBridge.GetStorage(address, index, stateRoot));
        }

        private ResultWrapper<UInt256?> GetAccountBalance(Address address, Keccak stateRoot)
        {
            Account account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<UInt256?>.Success(0);
            }

            return ResultWrapper<UInt256?>.Success(account.Balance);
        }

        private ResultWrapper<byte[]> GetAccountCode(Address address, Keccak stateRoot)
        {
            Account account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Bytes.Empty);
            }

            var code = _blockchainBridge.GetCode(account.CodeHash);
            return ResultWrapper<byte[]>.Success(code);
        }
    }
}