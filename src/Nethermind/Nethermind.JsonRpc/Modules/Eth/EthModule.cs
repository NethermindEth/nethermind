//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.PubSub.Models;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.TxPool;
using Block = Nethermind.Core.Block;
using BlockHeader = Nethermind.Core.BlockHeader;
using Signature = Nethermind.Core.Crypto.Signature;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModule : IEthModule
    {
        private Encoding _messageEncoding = Encoding.UTF8;

        private readonly IJsonRpcConfig _rpcConfig;
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly ITxPoolBridge _txPoolBridge;

        private readonly ILogger _logger;

        private bool HasStateForBlock(BlockHeader header)
        {
            RootCheckVisitor rootCheckVisitor = new RootCheckVisitor();
            _blockchainBridge.RunTreeVisitor(rootCheckVisitor, header.StateRoot);
            return rootCheckVisitor.HasRoot;
        }
        
        public EthModule(IJsonRpcConfig rpcConfig, IBlockchainBridge blockchainBridge, ITxPoolBridge txPoolBridge, ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
            _rpcConfig = rpcConfig ?? throw new ArgumentNullException(nameof(rpcConfig));
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _txPoolBridge = txPoolBridge ?? throw new ArgumentNullException(nameof(txPoolBridge));
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            return ResultWrapper<string>.Success("0x41");
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
            return ResultWrapper<bool?>.Success(_blockchainBridge.IsMining);
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
            long number = _blockchainBridge.Head?.Number ?? 0;
            return Task.FromResult(ResultWrapper<long?>.Success(number));
        }

        public Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockchainBridge.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail(searchResult));
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(header))
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable));
            }
            
            Account account = _blockchainBridge.GetAccount(address, header.StateRoot);
            return Task.FromResult(ResultWrapper<UInt256?>.Success(account?.Balance ?? UInt256.Zero));
        }

        public ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex, BlockParameter blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockchainBridge.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<byte[]>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object;
            Account account = _blockchainBridge.GetAccount(address, header.StateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Bytes.Empty);
            }

            return ResultWrapper<byte[]>.Success(_blockchainBridge.GetStorage(address, positionIndex, header.StateRoot));
        }

        public Task<ResultWrapper<UInt256?>> eth_getTransactionCount(Address address, BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> searchResult = _blockchainBridge.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail(searchResult));
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(header))
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable));
            }
            
            Account account = _blockchainBridge.GetAccount(address, header.StateRoot);
            return Task.FromResult(ResultWrapper<UInt256?>.Success(account?.Nonce ?? 0));
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256) searchResult.Object.Transactions.Length);
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256) searchResult.Object.Transactions.Length);
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256) searchResult.Object.Ommers.Length);
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256) searchResult.Object.Ommers.Length);
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockchainBridge.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<byte[]>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(header))
            {
                return ResultWrapper<byte[]>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
            }
            
            Account account = _blockchainBridge.GetAccount(address, header.StateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Bytes.Empty);
            }

            var code = _blockchainBridge.GetCode(account.CodeHash);
            return ResultWrapper<byte[]>.Success(code);
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
            catch (SecurityException e)
            {
                return ResultWrapper<byte[]>.Fail(e.Message, ErrorCodes.AccountLocked);
            }
            catch (Exception)
            {
                return ResultWrapper<byte[]>.Fail($"Unable to sign as {addressData}");
            }

            if (_logger.IsTrace) _logger.Trace($"eth_sign request {addressData}, {message}, result: {sig}");
            return ResultWrapper<byte[]>.Success(sig.Bytes);
        }

        public Task<ResultWrapper<Keccak>> eth_sendTransaction(TransactionForRpc transactionForRpc)
        {
            Transaction tx = transactionForRpc.ToTransactionWithDefaults();
            return SendTx(tx);
        }

        public async Task<ResultWrapper<Keccak>> eth_sendRawTransaction(byte[] transaction)
        {
            try
            {
                Transaction tx = Rlp.Decode<Transaction>(transaction, RlpBehaviors.AllowUnsigned);
                return await SendTx(tx);
            }
            catch (RlpException)
            {
                return ResultWrapper<Keccak>.Fail("Invalid RLP.", ErrorCodes.TransactionRejected);
            }
        }

        private Task<ResultWrapper<Keccak>> SendTx(Transaction tx)
        {
            try
            {
                Keccak txHash = _txPoolBridge.SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
                return Task.FromResult(ResultWrapper<Keccak>.Success(txHash));
            }
            catch (SecurityException e)
            {
                return Task.FromResult(ResultWrapper<Keccak>.Fail(e.Message, ErrorCodes.AccountLocked));
            }
            catch (Exception e)
            {
                return Task.FromResult(ResultWrapper<Keccak>.Fail(e.Message, ErrorCodes.TransactionRejected));
            }
        }

        public ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockchainBridge.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<string>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(header))
            {
                return ResultWrapper<string>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
            }
            
            FixCallTx(transactionCall, header);

            Transaction tx = transactionCall.ToTransaction();
            BlockchainBridge.CallOutput result = _blockchainBridge.Call(header, tx);

            return result.Error != null ? ResultWrapper<string>.Fail("VM execution error.", ErrorCodes.ExecutionError, result.Error) : ResultWrapper<string>.Success(result.OutputData.ToHexString(true));
        }

        private void FixCallTx(TransactionForRpc transactionCall, BlockHeader header)
        {
            if (transactionCall.Gas == null || transactionCall.Gas == 0)
            {
                transactionCall.Gas = Math.Min(_rpcConfig.GasCap ?? long.MaxValue, header.GasLimit);
            }
            else
            {
                transactionCall.Gas = Math.Min(_rpcConfig.GasCap ?? long.MaxValue, transactionCall.Gas.Value);
            }

            transactionCall.From ??= Address.SystemUser;
        }

        public ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall)
        {
            BlockHeader head = _blockchainBridge.FindLatestHeader();
            if (!HasStateForBlock(head))
            {
                return ResultWrapper<UInt256?>.Fail($"No state available for block {head.Hash}", ErrorCodes.ResourceUnavailable);
            }
            
            FixCallTx(transactionCall, head);

            BlockchainBridge.CallOutput result = _blockchainBridge.EstimateGas(head, transactionCall.ToTransaction());
            if (result.Error == null)
            {
                return ResultWrapper<UInt256?>.Success((UInt256) result.GasSpent);
            }

            return ResultWrapper<UInt256?>.Fail(result.Error);
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            return GetBlock(new BlockParameter(blockHash), returnFullTransactionObjects);
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            return GetBlock(blockParameter, returnFullTransactionObjects);
        }

        private ResultWrapper<BlockForRpc> GetBlock(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(blockParameter, true);
            if (searchResult.IsError)
            {
                return ResultWrapper<BlockForRpc>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            if (block != null)
            {
                _blockchainBridge.RecoverTxSenders(block);
            }

            return ResultWrapper<BlockForRpc>.Success(block == null ? null : new BlockForRpc(block, returnFullTransactionObjects));
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash)
        {
            Transaction transaction = _txPoolBridge.GetPendingTransaction(transactionHash);
            TxReceipt receipt = null; // note that if transaction is pending then for sure no receipt is known
            if (transaction == null)
            {
                (receipt, transaction) = _blockchainBridge.GetTransaction(transactionHash);
                if (transaction == null)
                {
                    return ResultWrapper<TransactionForRpc>.Success(null);
                }
            }

            RecoverTxSenderIfNeeded(transaction);
            TransactionForRpc transactionModel = new TransactionForRpc(receipt?.BlockHash, receipt?.BlockNumber, receipt?.Index, transaction);
            if (_logger.IsTrace) _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public ResultWrapper<TransactionForRpc[]> eth_pendingTransactions()
        {
            var transactions = _txPoolBridge.GetPendingTransactions();
            var transactionsModels = new TransactionForRpc[transactions.Length];
            for (int i = 0; i < transactions.Length; i++)
            {
                var transaction = transactions[i];
                RecoverTxSenderIfNeeded(transaction);
                transactionsModels[i] = new TransactionForRpc(transaction);
                transactionsModels[i].BlockHash = Keccak.Zero;
            }

            if (_logger.IsTrace) _logger.Trace($"eth_pendingTransactions request, result: {transactionsModels.Length}");
            return ResultWrapper<TransactionForRpc[]>.Success(transactionsModels);
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionForRpc>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
            }

            Transaction transaction = block.Transactions[(int) positionIndex];
            RecoverTxSenderIfNeeded(transaction);

            TransactionForRpc transactionModel = new TransactionForRpc(block.Hash, block.Number, (int) positionIndex, transaction);

            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionForRpc>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
            }

            Transaction transaction = block.Transactions[(int) positionIndex];
            RecoverTxSenderIfNeeded(transaction);

            TransactionForRpc transactionModel = new TransactionForRpc(block.Hash, block.Number, (int) positionIndex, transaction);

            if (_logger.IsDebug) _logger.Debug($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {transactionModel.Hash}");
            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public Task<ResultWrapper<ReceiptForRpc>> eth_getTransactionReceipt(Keccak txHash)
        {
            TxReceipt receipt = _blockchainBridge.GetReceipt(txHash);
            if (receipt == null)
            {
                return Task.FromResult(ResultWrapper<ReceiptForRpc>.Success(null));
            }

            ReceiptForRpc receiptModel = new ReceiptForRpc(txHash, receipt);
            if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
            return Task.FromResult(ResultWrapper<ReceiptForRpc>.Success(receiptModel));
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex)
        {
            return GetUncle(new BlockParameter(blockHash), positionIndex);
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
        {
            return GetUncle(blockParameter, positionIndex);
        }

        private ResultWrapper<BlockForRpc> GetUncle(BlockParameter blockParameter, UInt256 positionIndex)
        {
            SearchResult<Block> searchResult = _blockchainBridge.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<BlockForRpc>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
            {
                return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
            }

            BlockHeader ommerHeader = block.Ommers[(int) positionIndex];
            return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(new Block(ommerHeader, BlockBody.Empty), false));
        }

        public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
        {
            BlockParameter fromBlock = filter.FromBlock;
            BlockParameter toBlock = filter.ToBlock;
            int filterId = _blockchainBridge.NewFilter(fromBlock, toBlock, filter.Address, filter.Topics);
            return ResultWrapper<UInt256?>.Success((UInt256) filterId);
        }

        public ResultWrapper<UInt256?> eth_newBlockFilter()
        {
            int filterId = _blockchainBridge.NewBlockFilter();
            return ResultWrapper<UInt256?>.Success((UInt256) filterId);
        }

        public ResultWrapper<UInt256?> eth_newPendingTransactionFilter()
        {
            int filterId = _blockchainBridge.NewPendingTransactionFilter();
            return ResultWrapper<UInt256?>.Success((UInt256) filterId);
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
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetBlockFilterChanges(id))
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                case FilterType.PendingTransactionFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetPendingTransactionFilterChanges(id))
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
            int id = (int) filterId;

            return _blockchainBridge.FilterExists(id)
                ? ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetFilterLogs(id))
                : ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: '{filterId}' does not exist.");
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            BlockParameter fromBlock = filter.FromBlock;
            BlockParameter toBlock = filter.ToBlock;

            try
            {
                return ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetLogs(fromBlock, toBlock, filter.Address, filter.Topics));
            }
            catch (ArgumentException e)
            {
                switch (e.Message)
                {
                    case ILogFinder.NotFoundError: return ResultWrapper<IEnumerable<FilterLog>>.Fail(e.Message, ErrorCodes.ResourceNotFound);
                    default:
                        return ResultWrapper<IEnumerable<FilterLog>>.Fail(e.Message, ErrorCodes.InvalidParams);
                }
            }
        }

        public ResultWrapper<IEnumerable<byte[]>> eth_getWork()
        {
            return ResultWrapper<IEnumerable<byte[]>>.Fail("eth_getWork not supported", ErrorCodes.MethodNotFound);
        }

        public ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest)
        {
            return ResultWrapper<bool?>.Fail("eth_submitWork not supported", ErrorCodes.MethodNotFound, null);
        }

        public ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id)
        {
            return ResultWrapper<bool?>.Fail("eth_submitHashrate not supported", ErrorCodes.MethodNotFound, null);
        }

        // https://github.com/ethereum/EIPs/issues/1186	
        public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] storageKeys, BlockParameter blockParameter)
        {
            BlockHeader header;
            try
            {
                header = _blockchainBridge.FindHeader(blockParameter);
                if (header == null)
                {
                    return ResultWrapper<AccountProof>.Fail($"{blockParameter} block not found", ErrorCodes.ResourceNotFound, null);
                }
                
                if (!HasStateForBlock(header))
                {
                    return ResultWrapper<AccountProof>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
                }
            }
            catch (Exception ex)
            {
                return ResultWrapper<AccountProof>.Fail(ex.Message, ErrorCodes.InternalError, null);
            }

            AccountProofCollector accountProofCollector = new AccountProofCollector(accountAddress, storageKeys);
            _blockchainBridge.RunTreeVisitor(accountProofCollector, header.StateRoot);

            return ResultWrapper<AccountProof>.Success(accountProofCollector.BuildResult());
        }

        public ResultWrapper<long> eth_chainId()
        {
            try
            {
                long chainId = _blockchainBridge.GetChainId();
                return ResultWrapper<long>.Success(chainId);
            }
            catch (Exception ex)
            {
                return ResultWrapper<long>.Fail(ex.Message, ErrorCodes.InternalError, 0L);
            }
        }

        private void RecoverTxSenderIfNeeded(Transaction transaction)
        {
            if (transaction.SenderAddress == null)
            {
                _blockchainBridge.RecoverTxSender(transaction);
            }
        }
    }
}