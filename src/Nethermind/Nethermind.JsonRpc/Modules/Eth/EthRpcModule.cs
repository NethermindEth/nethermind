//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Block = Nethermind.Core.Block;
using BlockHeader = Nethermind.Core.BlockHeader;
using Signature = Nethermind.Core.Crypto.Signature;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public partial class EthRpcModule : IEthRpcModule
    {
        private readonly Encoding _messageEncoding = Encoding.UTF8;
        private readonly IJsonRpcConfig _rpcConfig;
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IBlockFinder _blockFinder;
        private readonly IStateReader _stateReader;
        private readonly ITxPool _txPoolBridge;
        private readonly ITxSender _txSender;
        private readonly IWallet _wallet;
        private readonly ISpecProvider _specProvider;
        private readonly ILogger _logger;
        private readonly IGasPriceOracle _gasPriceOracle;
        private readonly IFeeHistoryOracle _feeHistoryOracle;
        private static bool HasStateForBlock(IBlockchainBridge blockchainBridge, BlockHeader header)
        {
            RootCheckVisitor rootCheckVisitor = new();
            blockchainBridge.RunTreeVisitor(rootCheckVisitor, header.StateRoot);
            return rootCheckVisitor.HasRoot;
        }

        public EthRpcModule(
            IJsonRpcConfig rpcConfig,
            IBlockchainBridge blockchainBridge,
            IBlockFinder blockFinder,
            IStateReader stateReader,
            ITxPool txPool,
            ITxSender txSender,
            IWallet wallet,
            ILogManager logManager,
            ISpecProvider specProvider,
            IGasPriceOracle gasPriceOracle,
            IFeeHistoryOracle feeHistoryOracle)
        {
            _logger = logManager.GetClassLogger();
            _rpcConfig = rpcConfig ?? throw new ArgumentNullException(nameof(rpcConfig));
            _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            _txPoolBridge = txPool ?? throw new ArgumentNullException(nameof(txPool));
            _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
            _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            return ResultWrapper<string>.Success("0x41");
        }

        public ResultWrapper<SyncingResult> eth_syncing()
        {
            SyncingResult result;
            long bestSuggestedNumber = _blockFinder.FindBestSuggestedHeader().Number;

            long headNumberOrZero = _blockFinder.Head?.Number ?? 0;
            bool isSyncing = bestSuggestedNumber > headNumberOrZero + 8;

            if (isSyncing)
            {
                result = new SyncingResult
                {
                    CurrentBlock = headNumberOrZero,
                    HighestBlock = bestSuggestedNumber,
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

        public ResultWrapper<UInt256?> eth_gasPrice()
        {
            return ResultWrapper<UInt256?>.Success(_gasPriceOracle.GetGasPriceEstimate());
        }

        public ResultWrapper<FeeHistoryResults> eth_feeHistory(int blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
        {
            return _feeHistoryOracle.GetFeeHistory(blockCount, newestBlock, rewardPercentiles);
        }

        public ResultWrapper<IEnumerable<Address>> eth_accounts()
        {
            try
            {
                var result = _wallet.GetAccounts();
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
            long number = _blockchainBridge.BeamHead?.Number ?? 0;
            return Task.FromResult(ResultWrapper<long?>.Success(number));
        }

        public Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter? blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail(searchResult));
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(_blockchainBridge, header))
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail($"No state available for block {header.Hash}",
                    ErrorCodes.ResourceUnavailable));
            }

            Account account = _stateReader.GetAccount(header.StateRoot, address);
            return Task.FromResult(ResultWrapper<UInt256?>.Success(account?.Balance ?? UInt256.Zero));
        }

        public ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex,
            BlockParameter? blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<byte[]>.Fail(searchResult);
            }

            BlockHeader? header = searchResult.Object;
            Account account = _stateReader.GetAccount(header.StateRoot, address);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
            }

            byte[] storage = _stateReader.GetStorage(account.StorageRoot, positionIndex);
            return ResultWrapper<byte[]>.Success(storage.PadLeft(32));
        }

        public Task<ResultWrapper<UInt256?>> eth_getTransactionCount(Address address, BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail(searchResult));
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(_blockchainBridge, header))
            {
                return Task.FromResult(ResultWrapper<UInt256?>.Fail($"No state available for block {header.Hash}",
                    ErrorCodes.ResourceUnavailable));
            }

            Account account = _stateReader.GetAccount(header.StateRoot, address);
            UInt256 nonce = account?.Nonce ?? 0;

            return Task.FromResult(ResultWrapper<UInt256?>.Success(nonce));
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object.Transactions.Length);
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object.Transactions.Length);
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object.Ommers.Length);
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter? blockParameter)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<UInt256?>.Fail(searchResult);
            }

            return ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object.Ommers.Length);
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter? blockParameter = null)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<byte[]>.Fail(searchResult);
            }

            BlockHeader header = searchResult.Object;
            if (!HasStateForBlock(_blockchainBridge, header))
            {
                return ResultWrapper<byte[]>.Fail($"No state available for block {header.Hash}",
                    ErrorCodes.ResourceUnavailable);
            }

            Account account = _stateReader.GetAccount(header.StateRoot, address);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
            }

            var code = _stateReader.GetCode(account.CodeHash);
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
                sig = _wallet.Sign(Keccak.Compute(signatureText), address);
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

        public Task<ResultWrapper<Keccak>> eth_sendTransaction(TransactionForRpc rpcTx)
        {
            Transaction tx = rpcTx.ToTransactionWithDefaults(_blockchainBridge.GetChainId());
            TxHandlingOptions options = rpcTx.Nonce == null ? TxHandlingOptions.ManagedNonce : TxHandlingOptions.None;
            return SendTx(tx, options);
        }

        public async Task<ResultWrapper<Keccak>> eth_sendRawTransaction(byte[] transaction)
        {
            try
            {
                Transaction tx = Rlp.Decode<Transaction>(transaction, RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping);
                return await SendTx(tx);
            }
            catch (RlpException)
            {
                return ResultWrapper<Keccak>.Fail("Invalid RLP.", ErrorCodes.TransactionRejected);
            }
        }

        private async Task<ResultWrapper<Keccak>> SendTx(Transaction tx,
            TxHandlingOptions txHandlingOptions = TxHandlingOptions.None)
        {
            try
            {
                (Keccak txHash, AddTxResult? addTxResult) =
                    await _txSender.SendTransaction(tx, txHandlingOptions | TxHandlingOptions.PersistentBroadcast);

                return addTxResult == AddTxResult.Added
                    ? ResultWrapper<Keccak>.Success(txHash)
                    : ResultWrapper<Keccak>.Fail(addTxResult?.ToString() ?? string.Empty, ErrorCodes.TransactionRejected);
            }
            catch (SecurityException e)
            {
                return ResultWrapper<Keccak>.Fail(e.Message, ErrorCodes.AccountLocked);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Failed to send transaction.", e);
                return ResultWrapper<Keccak>.Fail(e.Message, ErrorCodes.TransactionRejected);
            }
        }

        public ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter? blockParameter = null) =>
            new CallTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig)
                .ExecuteTx(transactionCall, blockParameter);

        public ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall, BlockParameter blockParameter) =>
            new EstimateGasTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig)
                .ExecuteTx(transactionCall, blockParameter);

        public ResultWrapper<AccessListForRpc> eth_createAccessList(TransactionForRpc transactionCall, BlockParameter? blockParameter = null, bool optimize = true) =>
            new CreateAccessListTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig, optimize)
                .ExecuteTx(transactionCall, blockParameter);

        public ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            return GetBlock(new BlockParameter(blockHash), returnFullTransactionObjects);
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter,
            bool returnFullTransactionObjects)
        {
            return GetBlock(blockParameter, returnFullTransactionObjects);
        }

        private ResultWrapper<BlockForRpc> GetBlock(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter, true);
            if (searchResult.IsError)
            {
                return ResultWrapper<BlockForRpc>.Fail(searchResult);
            }

            Block? block = searchResult.Object;
            if (block != null)
            {
                _blockchainBridge.RecoverTxSenders(block);
            }

            return ResultWrapper<BlockForRpc>.Success(block == null
                ? null
                : new BlockForRpc(block, returnFullTransactionObjects, _specProvider));
        }

        public Task<ResultWrapper<TransactionForRpc>> eth_getTransactionByHash(Keccak transactionHash)
        {
            UInt256? baseFee = null;
            _txPoolBridge.TryGetPendingTransaction(transactionHash, out Transaction transaction);
            TxReceipt receipt = null; // note that if transaction is pending then for sure no receipt is known
            if (transaction == null)
            {
                (receipt, transaction, baseFee) = _blockchainBridge.GetTransaction(transactionHash);
                if (transaction == null)
                {
                    return Task.FromResult(ResultWrapper<TransactionForRpc>.Success(null));
                }
            }

            RecoverTxSenderIfNeeded(transaction);
            TransactionForRpc transactionModel =
                new(receipt?.BlockHash, receipt?.BlockNumber, receipt?.Index, transaction, baseFee);
            if (_logger.IsTrace)
                _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
            return Task.FromResult(ResultWrapper<TransactionForRpc>.Success(transactionModel));
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

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash,
            UInt256 positionIndex)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionForRpc>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
            }

            Transaction transaction = block.Transactions[(int)positionIndex];
            RecoverTxSenderIfNeeded(transaction);

            TransactionForRpc transactionModel = new(block.Hash, block.Number, (int)positionIndex, transaction, block.BaseFeePerGas);

            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter,
            UInt256 positionIndex)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<TransactionForRpc>.Fail(searchResult);
            }

            Block? block = searchResult.Object;
            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
            }

            Transaction transaction = block.Transactions[(int)positionIndex];
            RecoverTxSenderIfNeeded(transaction);

            TransactionForRpc transactionModel = new(block.Hash, block.Number, (int)positionIndex, transaction, block.BaseFeePerGas);

            if (_logger.IsDebug)
                _logger.Debug(
                    $"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {transactionModel.Hash}");
            return ResultWrapper<TransactionForRpc>.Success(transactionModel);
        }

        public Task<ResultWrapper<ReceiptForRpc>> eth_getTransactionReceipt(Keccak txHash)
        {
            var result = _blockchainBridge.GetReceiptAndEffectiveGasPrice(txHash);
            if (result.Receipt == null)
            {
                return Task.FromResult(ResultWrapper<ReceiptForRpc>.Success(null));
            }

            ReceiptForRpc receiptModel = new(txHash, result.Receipt, result.EffectiveGasPrice);
            if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
            return Task.FromResult(ResultWrapper<ReceiptForRpc>.Success(receiptModel));
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHash, UInt256 positionIndex)
        {
            return GetUncle(new BlockParameter(blockHash), positionIndex);
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter,
            UInt256 positionIndex)
        {
            return GetUncle(blockParameter, positionIndex);
        }

        private ResultWrapper<BlockForRpc> GetUncle(BlockParameter blockParameter, UInt256 positionIndex)
        {
            SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
            if (searchResult.IsError)
            {
                return ResultWrapper<BlockForRpc>.Fail(searchResult);
            }

            Block block = searchResult.Object;
            if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
            {
                return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
            }

            BlockHeader ommerHeader = block.Ommers[(int)positionIndex];
            return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(new Block(ommerHeader, BlockBody.Empty), false, _specProvider));
        }

        public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
        {
            BlockParameter fromBlock = filter.FromBlock;
            BlockParameter toBlock = filter.ToBlock;
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
            _blockchainBridge.UninstallFilter((int)filterId);
            return ResultWrapper<bool?>.Success(true);
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId)
        {
            int id = (int)filterId;
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
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge
                            .GetPendingTransactionFilterChanges(id))
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
            int id = (int)filterId;

            return _blockchainBridge.FilterExists(id)
                ? ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetFilterLogs(id))
                : ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: '{filterId}' does not exist.");
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            IEnumerable<FilterLog> GetLogs(BlockParameter blockParameter, BlockParameter toBlockParameter,
                CancellationTokenSource cancellationTokenSource, CancellationToken token)
            {
                using (cancellationTokenSource)
                {
                    foreach (FilterLog log in _blockchainBridge.GetLogs(blockParameter, toBlockParameter,
                        filter.Address, filter.Topics, token))
                    {
                        yield return log;
                    }
                }
            }

            BlockParameter fromBlock = filter.FromBlock;
            BlockParameter toBlock = filter.ToBlock;

            try
            {
                CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
                return ResultWrapper<IEnumerable<FilterLog>>.Success(GetLogs(fromBlock, toBlock,
                    cancellationTokenSource, cancellationTokenSource.Token));
            }
            catch (ArgumentException e)
            {
                switch (e.Message)
                {
                    case ILogFinder.NotFoundError:
                        return ResultWrapper<IEnumerable<FilterLog>>.Fail(e.Message, ErrorCodes.ResourceNotFound);
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
        public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] storageKeys,
            BlockParameter blockParameter)
        {
            BlockHeader header;
            try
            {
                header = _blockFinder.FindHeader(blockParameter);
                if (header == null)
                {
                    return ResultWrapper<AccountProof>.Fail($"{blockParameter} block not found",
                        ErrorCodes.ResourceNotFound, null);
                }

                if (!HasStateForBlock(_blockchainBridge, header))
                {
                    return ResultWrapper<AccountProof>.Fail($"No state available for block {header.Hash}",
                        ErrorCodes.ResourceUnavailable);
                }
            }
            catch (Exception ex)
            {
                return ResultWrapper<AccountProof>.Fail(ex.Message, ErrorCodes.InternalError, null);
            }

            AccountProofCollector accountProofCollector = new(accountAddress, storageKeys);
            _blockchainBridge.RunTreeVisitor(accountProofCollector, header.StateRoot);

            return ResultWrapper<AccountProof>.Success(accountProofCollector.BuildResult());
        }

        public ResultWrapper<ulong> eth_chainId()
        {
            try
            {
                ulong chainId = _blockchainBridge.GetChainId();
                return ResultWrapper<ulong>.Success(chainId);
            }
            catch (Exception ex)
            {
                return ResultWrapper<ulong>.Fail(ex.Message, ErrorCodes.InternalError, 0L);
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
