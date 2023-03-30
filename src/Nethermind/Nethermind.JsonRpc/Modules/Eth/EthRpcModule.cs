// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.Logging;
using Nethermind.Network.P2P;
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

namespace Nethermind.JsonRpc.Modules.Eth;

public partial class EthRpcModule : IEthRpcModule
{
    private readonly Encoding _messageEncoding = Encoding.UTF8;
    private readonly IJsonRpcConfig _rpcConfig;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly IBlockFinder _blockFinder;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IStateReader _stateReader;
    private readonly ITxPool _txPoolBridge;
    private readonly ITxSender _txSender;
    private readonly IWallet _wallet;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private readonly IGasPriceOracle _gasPriceOracle;
    private readonly IEthSyncingInfo _ethSyncingInfo;

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
        IReceiptFinder receiptFinder,
        ILogManager logManager,
        ISpecProvider specProvider,
        IGasPriceOracle gasPriceOracle,
        IEthSyncingInfo ethSyncingInfo,
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
        _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder)); ;
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _gasPriceOracle = gasPriceOracle ?? throw new ArgumentNullException(nameof(gasPriceOracle));
        _ethSyncingInfo = ethSyncingInfo ?? throw new ArgumentNullException(nameof(ethSyncingInfo));
        _feeHistoryOracle = feeHistoryOracle ?? throw new ArgumentNullException(nameof(feeHistoryOracle));
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

    public ResultWrapper<UInt256?> eth_maxPriorityFeePerGas()
    {
        UInt256 gasPriceWithBaseFee = _gasPriceOracle.GetMaxPriorityGasFeeEstimate();
        return ResultWrapper<UInt256?>.Success(gasPriceWithBaseFee);
    }

    public ResultWrapper<FeeHistoryResults> eth_feeHistory(long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
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
        if (account is null)
        {
            return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
        }

        byte[] storage = _stateReader.GetStorage(account.StorageRoot, positionIndex);
        return ResultWrapper<byte[]>.Success(storage.PadLeft(32));
    }

    public Task<ResultWrapper<UInt256>> eth_getTransactionCount(Address address, BlockParameter blockParameter)
    {

        if (blockParameter == BlockParameter.Pending)
        {
            UInt256 pendingNonce = _txPoolBridge.GetLatestPendingNonce(address);
            return Task.FromResult(ResultWrapper<UInt256>.Success(pendingNonce));

        }

        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return Task.FromResult(ResultWrapper<UInt256>.Fail(searchResult));
        }

        BlockHeader header = searchResult.Object;
        if (!HasStateForBlock(_blockchainBridge, header))
        {
            return Task.FromResult(ResultWrapper<UInt256>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable));
        }

        Account account = _stateReader.GetAccount(header.StateRoot, address);
        UInt256 nonce = account?.Nonce ?? 0;

        return Task.FromResult(ResultWrapper<UInt256>.Success(nonce));
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

        return ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object.Uncles.Length);
    }

    public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter? blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<UInt256?>.Fail(searchResult);
        }

        return ResultWrapper<UInt256?>.Success((UInt256)searchResult.Object.Uncles.Length);
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
        if (account is null)
        {
            return ResultWrapper<byte[]>.Success(Array.Empty<byte>());
        }

        byte[]? code = _stateReader.GetCode(account.CodeHash);
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
        TxHandlingOptions options = rpcTx.Nonce is null ? TxHandlingOptions.ManagedNonce : TxHandlingOptions.None;
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
            (Keccak txHash, AcceptTxResult? acceptTxResult) =
                await _txSender.SendTransaction(tx, txHandlingOptions | TxHandlingOptions.PersistentBroadcast);

            return acceptTxResult.Equals(AcceptTxResult.Accepted)
                ? ResultWrapper<Keccak>.Success(txHash)
                : ResultWrapper<Keccak>.Fail(acceptTxResult?.ToString() ?? string.Empty, ErrorCodes.TransactionRejected);
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
        if (returnFullTransactionObjects && block is not null)
        {
            _blockchainBridge.RecoverTxSenders(block);
        }

        return ResultWrapper<BlockForRpc>.Success(block is null
            ? null
            : new BlockForRpc(block, returnFullTransactionObjects, _specProvider));
    }

    public Task<ResultWrapper<TransactionForRpc>> eth_getTransactionByHash(Keccak transactionHash)
    {
        UInt256? baseFee = null;
        _txPoolBridge.TryGetPendingTransaction(transactionHash, out Transaction transaction);
        TxReceipt receipt = null; // note that if transaction is pending then for sure no receipt is known
        if (transaction is null)
        {
            (receipt, transaction, baseFee) = _blockchainBridge.GetTransaction(transactionHash);
            if (transaction is null)
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
        Transaction[] transactions = _txPoolBridge.GetPendingTransactions();
        TransactionForRpc[] transactionsModels = new TransactionForRpc[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction transaction = transactions[i];
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
        (TxReceipt receipt, UInt256? effectiveGasPrice, int logIndexStart) = _blockchainBridge.GetReceiptAndEffectiveGasPrice(txHash);
        if (receipt is null)
        {
            return Task.FromResult(ResultWrapper<ReceiptForRpc>.Success(null));
        }

        if (_logger.IsTrace) _logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
        return Task.FromResult(ResultWrapper<ReceiptForRpc>.Success(new(txHash, receipt, effectiveGasPrice, logIndexStart)));
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
        if (positionIndex < 0 || positionIndex > block.Uncles.Length - 1)
        {
            return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorCodes.InvalidParams);
        }

        BlockHeader uncleHeader = block.Uncles[(int)positionIndex];
        return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(new Block(uncleHeader), false, _specProvider));
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
                    return ResultWrapper<IEnumerable<object>>.Fail($"$Filter type {filterType} is not supported.", ErrorCodes.InvalidInput);
                }
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
    {
        CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        try
        {
            int id = filterId <= int.MaxValue ? (int)filterId : -1;
            bool filterFound = _blockchainBridge.TryGetLogs(id, out IEnumerable<FilterLog> filterLogs, cancellationToken);
            if (id < 0 || !filterFound)
            {
                cancellationTokenSource.Dispose();
                return ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: '{filterId}' does not exist.");
            }
            else
            {
                return ResultWrapper<IEnumerable<FilterLog>>.Success(GetLogs(filterLogs, cancellationTokenSource));
            }
        }
        catch (ResourceNotFoundException exception)
        {
            cancellationTokenSource.Dispose();
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(exception.Message, ErrorCodes.ResourceNotFound);
        }
    }

    public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
    {
        // because of lazy evaluation of enumerable, we need to do the validation here first
        CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        SearchResult<BlockHeader> fromBlockResult;
        SearchResult<BlockHeader> toBlockResult;

        if (filter.FromBlock == filter.ToBlock)
            fromBlockResult = toBlockResult = _blockFinder.SearchForHeader(filter.ToBlock);
        else
        {
            toBlockResult = _blockFinder.SearchForHeader(filter.ToBlock);

            if (toBlockResult.IsError)
            {
                cancellationTokenSource.Dispose();

                return ResultWrapper<IEnumerable<FilterLog>>.Fail(toBlockResult);
            }

            cancellationToken.ThrowIfCancellationRequested();

            fromBlockResult = _blockFinder.SearchForHeader(filter.FromBlock);
        }

        if (fromBlockResult.IsError)
        {
            cancellationTokenSource.Dispose();

            return ResultWrapper<IEnumerable<FilterLog>>.Fail(fromBlockResult);
        }

        cancellationToken.ThrowIfCancellationRequested();

        long fromBlockNumber = fromBlockResult.Object!.Number;
        long toBlockNumber = toBlockResult.Object!.Number;

        if (fromBlockNumber > toBlockNumber && toBlockNumber != 0)
        {
            cancellationTokenSource.Dispose();

            return ResultWrapper<IEnumerable<FilterLog>>.Fail($"'From' block '{fromBlockNumber}' is later than 'to' block '{toBlockNumber}'.", ErrorCodes.InvalidParams);
        }

        try
        {
            IEnumerable<FilterLog> filterLogs = _blockchainBridge.GetLogs(filter.FromBlock, filter.ToBlock,
                filter.Address, filter.Topics, cancellationToken);

            return ResultWrapper<IEnumerable<FilterLog>>.Success(GetLogs(filterLogs, cancellationTokenSource));
        }
        catch (ResourceNotFoundException exception)
        {
            return ResultWrapper<IEnumerable<FilterLog>>.Fail(exception.Message, ErrorCodes.ResourceNotFound);
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
    public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, UInt256[] storageKeys,
        BlockParameter blockParameter)
    {
        BlockHeader header;
        try
        {
            header = _blockFinder.FindHeader(blockParameter);
            if (header is null)
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
        transaction.SenderAddress ??= _blockchainBridge.RecoverTxSender(transaction);
    }

    private IEnumerable<FilterLog> GetLogs(IEnumerable<FilterLog> logs, CancellationTokenSource cancellationTokenSource)
    {
        using (cancellationTokenSource)
        {
            foreach (FilterLog log in logs)
            {
                yield return log;
            }
        }
    }

    public ResultWrapper<AccountForRpc> eth_getAccount(Address accountAddress, BlockParameter? blockParameter)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter ?? BlockParameter.Latest);
        if (searchResult.IsError)
        {
            // probably better forward Error of searchResult
            return ResultWrapper<AccountForRpc>.Fail($"header not found", ErrorCodes.InvalidInput);
        }
        BlockHeader header = searchResult.Object;
        if (!HasStateForBlock(_blockchainBridge, header))
        {
            return ResultWrapper<AccountForRpc>.Fail($"No state available for {blockParameter}",
                ErrorCodes.ResourceUnavailable);
        }
        Account account = _stateReader.GetAccount(header.StateRoot, accountAddress);
        return ResultWrapper<AccountForRpc>.Success(account is null ? null : new AccountForRpc(account));
    }
}
