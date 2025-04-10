// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.Reporting;
using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using DotNetty.Buffers;
using Nethermind.Blockchain.Receipts;
using Nethermind.TxPool;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugRpcModule : IDebugRpcModule
{
    private readonly IDebugBridge _debugBridge;
    private readonly ILogger _logger;
    private readonly IJsonRpcConfig _jsonRpcConfig;
    private readonly ISpecProvider _specProvider;
    private readonly BlockDecoder _blockDecoder;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly ulong _secondsPerSlot;
    private readonly IBlockFinder _blockFinder;


    public DebugRpcModule(
        ILogManager logManager,
        IDebugBridge debugBridge,
        IJsonRpcConfig jsonRpcConfig,
        ISpecProvider specProvider,
        IBlockchainBridge? blockchainBridge,
        ulong? secondsPerSlot,
        IBlockFinder? blockFinder
        )
    {
        _debugBridge = debugBridge ?? throw new ArgumentNullException(nameof(debugBridge));
        _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _logger = logManager.GetClassLogger();
        _blockDecoder = new BlockDecoder();
        _blockchainBridge = blockchainBridge ?? throw new ArgumentNullException(nameof(blockchainBridge));
        _secondsPerSlot = secondsPerSlot ?? throw new ArgumentNullException(nameof(secondsPerSlot));
        _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
    }

    public ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number)
    {
        try
        {
            ChainLevelInfo levelInfo = _debugBridge.GetLevelInfo(number);
            return levelInfo is null
                ? ResultWrapper<ChainLevelForRpc>.Fail($"Chain level {number} does not exist",
                    ErrorCodes.ResourceNotFound)
                : ResultWrapper<ChainLevelForRpc>.Success(new ChainLevelForRpc(levelInfo));
        }
        catch (Exception ex)
        {
            return ResultWrapper<ChainLevelForRpc>.Fail(ex);
        }
    }

    public ResultWrapper<int> debug_deleteChainSlice(in long startNumber, bool force = false)
    {
        try
        {
            int result = _debugBridge.DeleteChainSlice(startNumber, force);
            return ResultWrapper<int>.Success(result);
        }
        catch (Exception ex)
        {
            return ResultWrapper<int>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null)
    {
        try
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            Hash256? blockHash = _blockchainBridge.GetBlockHash(transactionHash);
            if (blockHash == null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"{blockHash} is null");
            }

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<GethLikeTxTrace>(block.Header);
            }

            GethLikeTxTrace? transactionTrace =
                _debugBridge.GetTransactionTrace(transactionHash, cancellationToken, options);
            if (transactionTrace is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {transactionHash}",
                    ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace)
                _logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }
        catch (Exception ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceCall(TransactionForRpc call, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        try
        {
            blockParameter ??= BlockParameter.Latest;
            call.EnsureDefaults(_jsonRpcConfig.GasCap);
            Transaction tx = call.ToTransaction();
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            GethLikeTxTrace transactionTrace =
                _debugBridge.GetTransactionTrace(tx, blockParameter, cancellationToken, options);
            if (transactionTrace is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {tx.Hash}",
                    ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {tx.Hash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }
        catch (Exception ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockhash, int index, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockhash));
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<GethLikeTxTrace>(block.Header);
            }

            var transactionTrace = _debugBridge.GetTransactionTrace(blockhash, index, cancellationToken, options);
            if (transactionTrace is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}",
                    ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace)
                _logger.Trace(
                    $"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }
        catch (Exception ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<GethLikeTxTrace>(block.Header);
            }

            long? blockNo = blockParameter.BlockNumber;
            if (!blockNo.HasValue)
            {
                throw new InvalidDataException("Block number value incorrect");
            }

            var transactionTrace = _debugBridge.GetTransactionTrace(blockNo.Value, index, cancellationToken, options);
            if (transactionTrace is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}",
                    ErrorCodes.ResourceNotFound);
            }

            if (_logger.IsTrace)
                _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }
        catch (Exception ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;


            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(blockRlp);

            if (!_blockchainBridge.HasStateForBlock(blockHeader))
            {
                return GetStateFailureResult<GethLikeTxTrace>(blockHeader);
            }


            var transactionTrace =
                _debugBridge.GetTransactionTrace(new Rlp(blockRlp), transactionHash, cancellationToken, options);
            if (transactionTrace is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail(
                    $"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}",
                    ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }
        catch (Exception ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(blockRlp);

            if (!_blockchainBridge.HasStateForBlock(blockHeader))
            {
                return GetStateFailureResult<GethLikeTxTrace>(blockHeader);
            }

            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);
            var transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
            if (transactionTrace is null)
            {
                return ResultWrapper<GethLikeTxTrace>.Fail(
                    $"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}",
                    ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
        }
        catch (Exception ex)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail(ex);
        }
    }

    public async Task<ResultWrapper<bool>> debug_migrateReceipts(long from, long to)
    {
        try
        {
            bool result = await _debugBridge.MigrateReceipts(from, to);
            return ResultWrapper<bool>.Success(result);
        }
        catch (Exception ex)
        {
            return ResultWrapper<bool>.Fail(ex);
        }
    }

    public Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc)
    {
        try
        {
            _debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(static r => r.ToReceipt()).ToArray());
            return Task.FromResult(ResultWrapper<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return ResultWrapper<bool>.Fail(ex);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
    {
        try{
            using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            BlockHeader blockHeader = Rlp.Decode<BlockHeader>(blockRlp);

            if (!_blockchainBridge.HasStateForBlock(blockHeader))
            {
                return GetStateFailureResult<IReadOnlyCollection<GethLikeTxTrace>>(blockHeader);
            }

            var blockTrace = _debugBridge.GetBlockTrace(new Rlp(blockRlp), cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (RlpException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Error decoding block rlp: {blockRlp.ToHexString()}", ErrorCodes.InvalidInput);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Couldn't find any block", ErrorCodes.InvalidInput);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(ex);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockNumber, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockNumber);
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IReadOnlyCollection<GethLikeTxTrace>>(block.Header);
            }

            IReadOnlyCollection<GethLikeTxTrace>? blockTrace = _debugBridge.GetBlockTrace(blockNumber, cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.InvalidInput);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(ex);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IReadOnlyCollection<GethLikeTxTrace>>(block.Header);
            }

            IReadOnlyCollection<GethLikeTxTrace>? blockTrace = _debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.InvalidInput);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail(ex);
        }
    }

    public ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null)
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<object> debug_dumpBlock(BlockParameter blockParameter)
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<GcStats> debug_gcStats()
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<byte[]> debug_getBlockRlp(long blockNumber)
    {
        try
        {
            byte[] rlp = _debugBridge.GetBlockRlp(new BlockParameter(blockNumber));
            if (rlp is null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {blockNumber} was not found", ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<byte[]>.Success(rlp);
        }
        catch (Exception ex)
        {
            return ResultWrapper<byte[]>.Fail(ex);
        }
    }

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash)
    {
        try
        {
            byte[] rlp = _debugBridge.GetBlockRlp(new BlockParameter(hash));
            if (rlp is null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {hash} was not found", ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<byte[]>.Success(rlp);
        }
        catch (Exception ex)
        {
            return ResultWrapper<byte[]>.Fail(ex);
        }
    }

    public ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter)
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter)
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<bool> debug_setHead(BlockParameter blockParameter)
    {
        throw new NotImplementedException();
    }

    public ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key)
    {
        var dbValue = _debugBridge.GetDbValue(dbName, key);
        return ResultWrapper<byte[]>.Success(dbValue);
    }

    public ResultWrapper<object> debug_getConfigValue(string category, string name)
    {
        try
        {
            var configValue = _debugBridge.GetConfigValue(category, name);
            return ResultWrapper<object>.Success(configValue);
        }
        catch (Exception ex)
        {
            return ResultWrapper<object>.Fail(ex);
        }
    }

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash)
    {
        try
        {
            _debugBridge.UpdateHeadBlock(blockHash);
            return ResultWrapper<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return ResultWrapper<bool>.Fail(ex);
        }
    }

    public ResultWrapper<string?> debug_getRawTransaction(Hash256 transactionHash)
    {
        try
        {
            Transaction? transaction = _debugBridge.GetTransactionFromHash(transactionHash);
            if (transaction is null)
            {
                return ResultWrapper<string?>.Fail($"Transaction {transactionHash} was not found",
                    ErrorCodes.ResourceNotFound);
            }

            RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping |
                                            (transaction.IsInMempoolForm()
                                                ? RlpBehaviors.InMempoolForm
                                                : RlpBehaviors.None);

            IByteBuffer buffer =
                PooledByteBufferAllocator.Default.Buffer(TxDecoder.Instance.GetLength(transaction, encodingSettings));
            using NettyRlpStream stream = new(buffer);
            TxDecoder.Instance.Encode(stream, transaction, encodingSettings);

            return ResultWrapper<string?>.Success(buffer.AsSpan().ToHexString(false));
        }
        catch (Exception ex)
        {
            return ResultWrapper<string?>.Fail(ex);
        }
    }

    public ResultWrapper<byte[][]> debug_getRawReceipts(BlockParameter blockParameter)
    {
        try
        {
            TxReceipt[] receipts = _debugBridge.GetReceiptsForBlock(blockParameter);
            if (receipts is null)
            {
                return ResultWrapper<byte[][]>.Fail($"Receipts are not found for block {blockParameter}",
                    ErrorCodes.ResourceNotFound);
            }

            if (receipts.Length == 0)
            {
                return ResultWrapper<byte[][]>.Success([]);
            }

            RlpBehaviors behavior =
                (_specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled
                    ? RlpBehaviors.Eip658Receipts
                    : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
            var rlp = receipts.Select(tx => Rlp.Encode(tx, behavior).Bytes);
            return ResultWrapper<byte[][]>.Success(rlp.ToArray());
        }
        catch (Exception ex)
        {
            return ResultWrapper<byte[][]>.Fail(ex);
        }
    }

    public ResultWrapper<byte[]> debug_getRawBlock(BlockParameter blockParameter)
    {
        try
        {
            var blockRLP = _debugBridge.GetBlockRlp(blockParameter);
            if (blockRLP is null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
            }

            return ResultWrapper<byte[]>.Success(blockRLP);
        }
        catch (Exception ex)
        {
            return ResultWrapper<byte[]>.Fail(ex);
        }
    }

    public ResultWrapper<byte[]> debug_getRawHeader(BlockParameter blockParameter)
    {
        try
        {
            var block = _debugBridge.GetBlock(blockParameter);
            if (block is null)
            {
                return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
            }

            Rlp rlp = Rlp.Encode<BlockHeader>(block.Header);
            return ResultWrapper<byte[]>.Success(rlp.Bytes);
        }
        catch (Exception ex)
        {
            return ResultWrapper<byte[]>.Fail(ex);
        }
    }

    public Task<ResultWrapper<SyncReportSymmary>> debug_getSyncStage()
    {
        try
        {
            SyncReportSymmary result = _debugBridge.GetCurrentSyncStage();
            return ResultWrapper<SyncReportSymmary>.Success(result);
        }
        catch (Exception ex)
        {
            return ResultWrapper<SyncReportSymmary>.Fail(ex);
        }
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<IEnumerable<string>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<string>>(block.Header);
            }

            IEnumerable<string>? files = _debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);

            if (_logger.IsTrace)
                _logger.Trace($"{nameof(debug_standardTraceBlockToFile)} request {blockHash}, result: {files}");

            return ResultWrapper<IEnumerable<string>>.Success(files);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IEnumerable<string>>.Fail(ex);
        }
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        try
        {
            using CancellationTokenSource cancellationTokenSource = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = cancellationTokenSource.Token;

            SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(new BlockParameter(blockHash));
            if (blockSearch.IsError || blockSearch.Object is null)
            {
                return ResultWrapper<IEnumerable<string>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<string>>(block.Header);
            }

            IEnumerable<string>? files = _debugBridge.TraceBadBlockToFile(blockHash, cancellationToken, options);

            if (_logger.IsTrace)
                _logger.Trace($"{nameof(debug_standardTraceBadBlockToFile)} request {blockHash}, result: {files}");

            return ResultWrapper<IEnumerable<string>>.Success(files);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IEnumerable<string>>.Fail(ex);
        }
    }

    public ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks()
    {
        try
        {
            IEnumerable<BadBlock> badBlocks = _debugBridge.GetBadBlocks()
                .Select(block => new BadBlock(block, true, _specProvider, _blockDecoder));
            return ResultWrapper<IEnumerable<BadBlock>>.Success(badBlocks);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IEnumerable<BadBlock>>.Fail(ex);
        }
    }

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        _jsonRpcConfig.BuildTimeoutCancellationToken();

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> debug_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        try
        {
            Block block;

            if (blockParameter == null)
            {
                block = _blockFinder.FindHeadBlock();
                if (block == null)
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>>.Fail(
                        "Block parameter is null and no Head Block found");
                }
            }
            else
            {
                SearchResult<Block> blockSearch = _blockFinder.SearchForBlock(blockParameter);
                if (blockSearch.IsError || blockSearch.Object is null)
                {
                    return ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>>.Fail(blockSearch);
                }

                block = blockSearch.Object!;
            }

            if (!_blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>>(block.Header);
            }

            return new SimulateTxExecutor<GethLikeTxTrace>(_blockchainBridge, _blockFinder, _jsonRpcConfig,
                    new GethStyleSimulateBlockTracerFactory(options: options ?? GethTraceOptions.Default),
                    _secondsPerSlot)
                .Execute(payload, blockParameter);
        }
        catch (Exception ex)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>>.Fail(ex);
        }

    }

    private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

}
