// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Trie;
using Nethermind.TxPool;
using Nethermind.Wallet;
using Block = Nethermind.Core.Block;
using BlockHeader = Nethermind.Core.BlockHeader;
using ResultType = Nethermind.Core.ResultType;
using Signature = Nethermind.Core.Crypto.Signature;
using Transaction = Nethermind.Core.Transaction;

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule(
    IJsonRpcConfig rpcConfig,
    IBlockchainBridge blockchainBridge,
    IBlockFinder blockFinder,
    IReceiptFinder receiptFinder,
    IStateReader stateReader,
    ITxPool txPool,
    ITxSender txSender,
    IWallet wallet,
    ILogManager logManager,
    ISpecProvider specProvider,
    IGasPriceOracle gasPriceOracle,
    IEthSyncingInfo ethSyncingInfo,
    IFeeHistoryOracle feeHistoryOracle,
    ulong? secondsPerSlot) : IEthRpcModule
{
    protected readonly Encoding _messageEncoding = Encoding.UTF8;
    protected readonly IJsonRpcConfig _rpcConfig = rpcConfig ?? throw new ArgumentNullException(nameof(rpcConfig));
    protected readonly IBlockchainBridge _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
    protected readonly IBlockFinder _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
    protected readonly IReceiptFinder _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
    protected readonly IStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
    protected readonly ITxPool _txPool = txPool ?? throw new ArgumentNullException(nameof(txPool));
    protected readonly ITxSender _txSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
    protected readonly IWallet _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
    protected readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    protected readonly ILogger _logger = logManager.GetClassLogger();
    protected readonly IGasPriceOracle _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
    protected readonly IEthSyncingInfo _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
    protected readonly IFeeHistoryOracle _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));
    protected readonly ulong _secondsPerSlot = secondsPerSlot ?? throw new ArgumentNullException(nameof(secondsPerSlot));

    private static bool HasStateForBlock(IBlockchainBridge blockchainBridge, BlockHeader header)
    {
        return blockchainBridge.HasStateForRoot(header.StateRoot!);
    }

    public ResultWrapper<string> eth_protocolVersion()
    {
        int highestVersion = P2PProtocolInfoProvider.GetHighestVersionOfEthProtocol();
        return ResultWrapper<string>.Success(highestVersion.ToHexString());
    }

    public ResultWrapper<SyncingResult> eth_syncing()
    {
        return ResultWrapper<SyncingResult>.Success(_ethSyncingInfo.GetFullInfo());
    }

    public ResultWrapper<byte[]> eth_snapshot()
    {
        return ResultWrapper<byte[]>.Fail("eth_snapshot not supported");
    }

    public ResultWrapper<Address> eth_coinbase()
    {
        return ResultWrapper<Address>.Success(Address.Zero);
    }

    public ResultWrapper<UInt256?> eth_gasPrice()
    {
        return ResultWrapper<UInt256?>.Success(_gasPriceOracle.GetGasPriceEstimate());
    }

    public ResultWrapper<UInt256?> eth_blobBaseFee()
    {
        if (_blockFinder.Head?.Header?.ExcessBlobGas is null)
        {
            return ResultWrapper<UInt256?>.Success(UInt256.Zero);
        }

        IReleaseSpec spec = _specProvider.GetSpec(_blockFinder.Head?.Header!);
        if (!BlobGasCalculator.TryCalculateFeePerBlobGas(_blockFinder.Head?.Header?.ExcessBlobGas ?? 0,
                spec.BlobBaseFeeUpdateFraction, out UInt256 feePerBlobGas))
        {
            return ResultWrapper<UInt256?>.Fail("Unable to calculate the current blob base fee");
        }
        return ResultWrapper<UInt256?>.Success(feePerBlobGas);
    }

    public ResultWrapper<UInt256?> eth_maxPriorityFeePerGas()
    {
        UInt256 gasPriceWithBaseFee = _gasPriceOracle.GetMaxPriorityGasFeeEstimate();
        return ResultWrapper<UInt256?>.Success(gasPriceWithBaseFee);
    }

    public ResultWrapper<FeeHistoryResults> eth_feeHistory(int blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
    {
        return _feeHistoryOracle.GetFeeHistory(blockCount, newestBlock, rewardPercentiles);
    }

    public ResultWrapper<IEnumerable<Address>> eth_accounts()
    {
        try
        {
            Address[] result = _wallet.GetAccounts();
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
        long number = _blockchainBridge.HeadBlock?.Number ?? 0;
        return Task.FromResult(ResultWrapper<long?>.Success(number));
    }

    public Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return Task.FromResult(GetFailureResult<UInt256?, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet()));
        }

        BlockHeader header = searchResult.Object;
        if (!HasStateForBlock(_blockchainBridge, header!))
        {
            return Task.FromResult(GetStateFailureResult<UInt256?>(header));
        }

        _stateReader.TryGetAccount(header!.StateRoot!, address, out AccountStruct account);
        return Task.FromResult(ResultWrapper<UInt256?>.Success(account.Balance));
    }

    public ResultWrapper<byte[]> eth_getStorageAt(Address address, UInt256 positionIndex,
        BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<byte[], BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader? header = searchResult.Object;
        try
        {
            ReadOnlySpan<byte> storage = _stateReader.GetStorage(header!.StateRoot!, address, positionIndex);
            return ResultWrapper<byte[]>.Success(storage.IsEmpty ? Bytes32.Zero.Unwrap() : storage!.PadLeft(32));
        }
        catch (MissingTrieNodeException e)
        {
            var hash = e.Hash;
            return ResultWrapper<byte[]>.Fail($"missing trie node {hash} (path ) state {hash} is not available", ErrorCodes.InvalidInput);
        }
    }

    public Task<ResultWrapper<UInt256>> eth_getTransactionCount(Address address, BlockParameter? blockParameter)
    {
        if (blockParameter == BlockParameter.Pending)
        {
            UInt256 pendingNonce = _txPool.GetLatestPendingNonce(address);
            return Task.FromResult(ResultWrapper<UInt256>.Success(pendingNonce));
        }

        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return Task.FromResult(GetFailureResult<UInt256, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet()));
        }

        BlockHeader header = searchResult.Object;
        if (!HasStateForBlock(_blockchainBridge, header!))
        {
            return Task.FromResult(GetStateFailureResult<UInt256>(header));
        }

        _stateReader.TryGetAccount(header!.StateRoot!, address, out AccountStruct account);
        return Task.FromResult(ResultWrapper<UInt256>.Success(account.Nonce));
    }

    public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Hash256 blockHash)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Transactions.Length);
    }

    public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Transactions.Length);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Hash256 blockHash)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Uncles.Length);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter? blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        return searchResult.IsError
            ? ResultWrapper<UInt256?>.Success(null)
            : ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object!.Uncles.Length);
    }

    public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter? blockParameter = null)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<byte[], BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object;
        return !HasStateForBlock(_blockchainBridge, header!)
            ? GetStateFailureResult<byte[]>(header)
            : ResultWrapper<byte[]>.Success(
                _stateReader.TryGetAccount(header!.StateRoot!, address, out AccountStruct account)
                    ? _stateReader.GetCode(account.CodeHash)
                    : []);
    }

    public ResultWrapper<string> eth_sign(Address addressData, byte[] message)
    {
        Signature sig;
        try
        {
            sig = _wallet.SignMessage(message, addressData);
        }
        catch (SecurityException e)
        {
            return ResultWrapper<string>.Fail(e.Message, ErrorCodes.AccountLocked);
        }
        catch (Exception)
        {
            return ResultWrapper<string>.Fail($"Unable to sign as {addressData}");
        }

        if (_logger.IsTrace) _logger.Trace($"eth_sign request {addressData}, {message}, result: {sig}");
        return ResultWrapper<string>.Success(sig.ToString());
    }

    public virtual Task<ResultWrapper<Hash256>> eth_sendTransaction(TransactionForRpc rpcTx)
    {
        Transaction tx = rpcTx.ToTransaction();
        tx.ChainId = _blockchainBridge.GetChainId();

        UInt256? nonce = rpcTx is LegacyTransactionForRpc legacy ? legacy.Nonce : null;

        TxHandlingOptions options = nonce is null ? TxHandlingOptions.ManagedNonce : TxHandlingOptions.None;
        return SendTx(tx, options);
    }

    public virtual async Task<ResultWrapper<Hash256>> eth_sendRawTransaction(byte[] transaction)
    {
        try
        {
            Transaction tx = Rlp.Decode<Transaction>(transaction,
                RlpBehaviors.AllowUnsigned | RlpBehaviors.SkipTypedWrapping | RlpBehaviors.InMempoolForm);
            return await SendTx(tx);
        }
        catch (RlpException)
        {
            return ResultWrapper<Hash256>.Fail("Invalid RLP.", ErrorCodes.TransactionRejected);
        }
    }

    private async Task<ResultWrapper<Hash256>> SendTx(Transaction tx,
        TxHandlingOptions txHandlingOptions = TxHandlingOptions.None)
    {
        try
        {
            (Hash256 txHash, AcceptTxResult? acceptTxResult) =
                await _txSender.SendTransaction(tx, txHandlingOptions | TxHandlingOptions.PersistentBroadcast);

            return acceptTxResult.Equals(AcceptTxResult.Accepted)
                ? ResultWrapper<Hash256>.Success(txHash)
                : ResultWrapper<Hash256>.Fail(acceptTxResult?.ToString() ?? string.Empty, ErrorCodes.TransactionRejected);
        }
        catch (SecurityException e)
        {
            return ResultWrapper<Hash256>.Fail(e.Message, ErrorCodes.AccountLocked);
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Failed to send transaction.", e);
            return ResultWrapper<Hash256>.Fail(e.Message, ErrorCodes.TransactionRejected);
        }
    }

    public ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null) =>
        new CallTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig)
            .ExecuteTx(transactionCall, blockParameter, stateOverride);

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> eth_simulateV1(SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null) =>
        new SimulateTxExecutor<SimulateCallResult>(_blockchainBridge, _blockFinder, _rpcConfig, new SimulateBlockMutatorTracerFactory())
            .Execute(payload, blockParameter);

    public ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall, BlockParameter? blockParameter, Dictionary<Address, AccountOverride>? stateOverride = null) =>
        new EstimateGasTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig)
            .ExecuteTx(transactionCall, blockParameter, stateOverride);

    public ResultWrapper<AccessListResultForRpc?> eth_createAccessList(TransactionForRpc transactionCall, BlockParameter? blockParameter = null, bool optimize = true) =>
        new CreateAccessListTxExecutor(_blockchainBridge, _blockFinder, _rpcConfig, optimize)
            .ExecuteTx(transactionCall, blockParameter);

    public ResultWrapper<BlockForRpc> eth_getBlockByHash(Hash256 blockHash, bool returnFullTransactionObjects)
    {
        return GetBlock(new BlockParameter(blockHash), returnFullTransactionObjects);
    }

    public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter,
        bool returnFullTransactionObjects)
    {
        return GetBlock(blockParameter, returnFullTransactionObjects);
    }

    protected virtual ResultWrapper<BlockForRpc?> GetBlock(BlockParameter blockParameter, bool returnFullTransactionObjects)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter, true);
        if (searchResult.IsError)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (returnFullTransactionObjects && block is not null)
        {
            _blockchainBridge.RecoverTxSenders(block);
        }

        return ResultWrapper<BlockForRpc?>.Success(block is null
            ? null
            : new BlockForRpc(block, returnFullTransactionObjects, _specProvider));
    }

    public virtual ResultWrapper<TransactionForRpc?> eth_getTransactionByHash(Hash256 transactionHash)
    {
        (TxReceipt? receipt, Transaction? transaction, UInt256? baseFee) = _blockchainBridge.GetTransaction(transactionHash, checkTxnPool: true);
        if (transaction is null)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        RecoverTxSenderIfNeeded(transaction);
        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(transaction, receipt?.BlockHash, receipt?.BlockNumber, receipt?.Index, baseFee, _specProvider.ChainId);
        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
    }

    public ResultWrapper<string?> eth_getRawTransactionByHash(Hash256 transactionHash)
    {
        Transaction? transaction = _blockchainBridge.GetTransaction(transactionHash, checkTxnPool: true).Transaction;
        if (transaction is null)
        {
            return ResultWrapper<string?>.Success(null);
        }

        RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping | (transaction.IsInMempoolForm() ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);

        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(TxDecoder.Instance.GetLength(transaction, encodingSettings));
        using NettyRlpStream stream = new(buffer);
        TxDecoder.Instance.Encode(stream, transaction, encodingSettings);

        return ResultWrapper<string?>.Success(buffer.AsSpan().ToHexString(false));
    }

    public ResultWrapper<TransactionForRpc[]> eth_pendingTransactions()
    {
        Transaction[] transactions = _txPool.GetPendingTransactions();
        TransactionForRpc[] transactionsModels = new TransactionForRpc[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction transaction = transactions[i];
            RecoverTxSenderIfNeeded(transaction);
            transactionsModels[i] = TransactionForRpc.FromTransaction(transaction, chainId: _specProvider.ChainId);
            transactionsModels[i].BlockHash = Keccak.Zero;
        }

        if (_logger.IsTrace) _logger.Trace($"eth_pendingTransactions request, result: {transactionsModels.Length}");
        return ResultWrapper<TransactionForRpc[]>.Success(transactionsModels);
    }

    public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Hash256 blockHash, UInt256 positionIndex)
    {
        ResultWrapper<TransactionForRpc> result = GetTransactionByBlockAndIndex(new BlockParameter(blockHash), positionIndex);
        if (_logger.IsTrace && result.Result.ResultType == ResultType.Success) _logger.Trace($"eth_getTransactionByBlockHashAndIndex request {blockHash}, index: {positionIndex}, result: {result.Data?.Hash}");
        return result;
    }

    public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        ResultWrapper<TransactionForRpc> result = GetTransactionByBlockAndIndex(blockParameter, positionIndex);
        if (_logger.IsTrace && result.Result.ResultType == ResultType.Success) _logger.Trace($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {result.Data?.Hash}");
        return result;
    }

    protected virtual ResultWrapper<TransactionForRpc?> GetTransactionByBlockAndIndex(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
        {
            return ResultWrapper<TransactionForRpc?>.Success(null);
        }

        Transaction transaction = block.Transactions[(int)positionIndex];
        RecoverTxSenderIfNeeded(transaction);

        TransactionForRpc transactionModel = TransactionForRpc.FromTransaction(transaction, block.Hash, block.Number, (int)positionIndex, block.BaseFeePerGas, _specProvider.ChainId);
        return ResultWrapper<TransactionForRpc?>.Success(transactionModel);
    }

    public ResultWrapper<BlockForRpc?> eth_getUncleByBlockHashAndIndex(Hash256 blockHash, UInt256 positionIndex)
    {
        return GetUncle(new BlockParameter(blockHash), positionIndex);
    }

    public ResultWrapper<BlockForRpc?> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter,
        UInt256 positionIndex)
    {
        return GetUncle(blockParameter, positionIndex);
    }

    private ResultWrapper<BlockForRpc?> GetUncle(BlockParameter blockParameter, UInt256 positionIndex)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        Block block = searchResult.Object!;
        if (positionIndex < 0 || positionIndex > block.Uncles.Length - 1)
        {
            return ResultWrapper<BlockForRpc?>.Success(null);
        }

        BlockHeader uncleHeader = block.Uncles[(int)positionIndex];
        return ResultWrapper<BlockForRpc?>.Success(new BlockForRpc(new Block(uncleHeader), false, _specProvider));
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
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter not found", ErrorCodes.InvalidInput);
                }
            case FilterType.PendingTransactionFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetPendingTransactionFilterChanges(id))
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter not found", ErrorCodes.InvalidInput);
                }
            case FilterType.LogFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetLogFilterChanges(id).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter not found", ErrorCodes.InvalidInput);
                }
            default:
                {
                    return ResultWrapper<IEnumerable<object>>.Fail($"Filter type {filterType} is not supported.", ErrorCodes.InvalidInput);
                }
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
    {
        CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        try
        {
            int id = filterId <= int.MaxValue ? (int)filterId : -1;
            bool filterFound = _blockchainBridge.TryGetLogs(id, out IEnumerable<FilterLog> filterLogs, cancellationToken);
            if (id < 0 || !filterFound)
            {
                timeout.Dispose();
                return ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: {filterId} does not exist.");
            }
            else
            {
                return ResultWrapper<IEnumerable<FilterLog>>.Success(GetLogs(filterLogs, timeout));
            }
        }
        catch (ResourceNotFoundException exception)
        {
            timeout.Dispose();
            return GetFailureResult<IEnumerable<FilterLog>>(exception, _ethSyncingInfo.SyncMode.HaveNotSyncedReceiptsYet());
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
    {
        BlockParameter fromBlock = filter.FromBlock;
        BlockParameter toBlock = filter.ToBlock;

        // because of lazy evaluation of enumerable, we need to do the validation here first
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        if (!TryFindBlockHeaderOrUseLatest(_blockFinder, ref toBlock, out SearchResult<BlockHeader> toBlockResult, out long? sourceToBlockNumber))
        {
            return FailWithNoHeadersSyncedYet(toBlockResult);
        }

        cancellationToken.ThrowIfCancellationRequested();

        SearchResult<BlockHeader> fromBlockResult;
        long? sourceFromBlockNumber;

        if (fromBlock == toBlock)
        {
            fromBlockResult = toBlockResult;
            sourceFromBlockNumber = sourceToBlockNumber;
        }
        else if (!TryFindBlockHeaderOrUseLatest(_blockFinder, ref fromBlock, out fromBlockResult, out sourceFromBlockNumber))
        {
            return FailWithNoHeadersSyncedYet(fromBlockResult);
        }

        if (sourceFromBlockNumber > sourceToBlockNumber)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Fail("invalid block range params", ErrorCodes.InvalidParams);
        }

        if (_blockFinder.Head?.Number is not null && sourceFromBlockNumber > _blockFinder.Head.Number)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Success([]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        BlockHeader fromBlockHeader = fromBlockResult.Object;
        BlockHeader toBlockHeader = toBlockResult.Object;

        try
        {
            LogFilter logFilter = _blockchainBridge.GetFilter(fromBlock, toBlock, filter.Address, filter.Topics);

            IEnumerable<FilterLog> filterLogs = _blockchainBridge.GetLogs(logFilter, fromBlockHeader, toBlockHeader, cancellationToken);

            ArrayPoolList<FilterLog> logs = new(_rpcConfig.MaxLogsPerResponse);

            foreach (FilterLog log in filterLogs)
            {
                logs.Add(log);
                if (JsonRpcContext.Current.Value?.IsAuthenticated != true // not authenticated
                    && _rpcConfig.MaxLogsPerResponse != 0                 // not unlimited
                    && logs.Count > _rpcConfig.MaxLogsPerResponse)
                {
                    logs.Dispose();
                    return ResultWrapper<IEnumerable<FilterLog>>.Fail($"Too many logs requested. Max logs per response is {_rpcConfig.MaxLogsPerResponse}.", ErrorCodes.LimitExceeded);
                }
            }

            return ResultWrapper<IEnumerable<FilterLog>>.Success(logs);
        }
        catch (ResourceNotFoundException exception)
        {
            return GetFailureResult<IEnumerable<FilterLog>>(exception, _ethSyncingInfo.SyncMode.HaveNotSyncedReceiptsYet());
        }

        ResultWrapper<IEnumerable<FilterLog>> FailWithNoHeadersSyncedYet(SearchResult<BlockHeader> blockResult)
            => GetFailureResult<IEnumerable<FilterLog>, BlockHeader>(blockResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());

        // If there is an error, we check if we seach by number and it's after the head, then try to use head instead
        static bool TryFindBlockHeaderOrUseLatest(IBlockFinder blockFinder, ref BlockParameter blockParameter, out SearchResult<BlockHeader> blockResult, out long? sourceBlockNumber)
        {
            blockResult = blockFinder.SearchForHeader(blockParameter);

            if (blockResult.IsError)
            {
                if (blockParameter.Type is BlockParameterType.BlockNumber &&
                    blockFinder.Head?.Number < blockParameter.BlockNumber)
                {
                    blockResult = new SearchResult<BlockHeader>(blockFinder.Head.Header);

                    sourceBlockNumber = blockParameter.BlockNumber.Value;
                    return true;
                }

                sourceBlockNumber = null;
                return false;
            }

            sourceBlockNumber = blockResult.Object.Number;
            return true;
        }
    }

    // https://github.com/ethereum/EIPs/issues/1186
    public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, UInt256[] storageKeys,
        BlockParameter blockParameter)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<AccountProof, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object;

        if (!HasStateForBlock(_blockchainBridge, header!))
        {
            return GetStateFailureResult<AccountProof>(header);
        }

        AccountProofCollector accountProofCollector = new(accountAddress, storageKeys);
        _blockchainBridge.RunTreeVisitor(accountProofCollector, header!.StateRoot!);
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

    protected void RecoverTxSenderIfNeeded(Transaction transaction)
    {
        transaction.SenderAddress ??= _blockchainBridge.RecoverTxSender(transaction);
    }

    private static IEnumerable<FilterLog> GetLogs(IEnumerable<FilterLog> logs, CancellationTokenSource cancellationTokenSource)
    {
        using (cancellationTokenSource)
        {
            foreach (FilterLog log in logs)
            {
                yield return log;
            }
        }
    }

    public ResultWrapper<AccountForRpc?> eth_getAccount(Address accountAddress, BlockParameter? blockParameter)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return GetFailureResult<AccountForRpc?, BlockHeader>(searchResult, _ethSyncingInfo.SyncMode.HaveNotSyncedHeadersYet());
        }

        BlockHeader header = searchResult.Object;
        return !HasStateForBlock(_blockchainBridge, header!)
            ? GetStateFailureResult<AccountForRpc?>(header)
            : ResultWrapper<AccountForRpc?>.Success(
                _stateReader.TryGetAccount(header!.StateRoot!, accountAddress, out AccountStruct account)
                    ? new AccountForRpc(account)
                    : null);
    }

    protected static ResultWrapper<TResult> GetFailureResult<TResult, TSearch>(SearchResult<TSearch> searchResult, bool isTemporary) where TSearch : class =>
        ResultWrapper<TResult>.Fail(searchResult, isTemporary && searchResult.ErrorCode == ErrorCodes.ResourceNotFound);

    private static ResultWrapper<TResult> GetFailureResult<TResult>(ResourceNotFoundException exception, bool isTemporary) =>
        ResultWrapper<TResult>.Fail(exception.Message, ErrorCodes.ResourceNotFound, isTemporary);

    private ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable, _ethSyncingInfo.SyncMode.HaveNotSyncedStateYet());

    public virtual ResultWrapper<ReceiptForRpc?> eth_getTransactionReceipt(Hash256 txHash)
    {
        (TxReceipt? receipt, TxGasInfo? gasInfo, int logIndexStart) = _blockchainBridge.GetReceiptAndGasInfo(txHash);
        if (receipt is null || gasInfo is null)
        {
            return ResultWrapper<ReceiptForRpc>.Success(null);
        }

        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
        return ResultWrapper<ReceiptForRpc>.Success(new(txHash, receipt, gasInfo.Value, logIndexStart));
    }

    public virtual ResultWrapper<ReceiptForRpc[]?> eth_getBlockReceipts(BlockParameter blockParameter)
    {
        SearchResult<Block> searchResult = blockFinder.SearchForBlock(blockParameter);
        return searchResult switch
        {
            { IsError: true } => ResultWrapper<ReceiptForRpc[]?>.Success(null),
            _ => _receiptFinder.GetBlockReceipts(blockParameter, _blockFinder, _specProvider)
        };
    }

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        _rpcConfig.BuildTimeoutCancellationToken();
}
