// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization.Reporting;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class DebugRpcModule(

    ILogManager logManager,
    IDebugBridge debugBridge,
    IJsonRpcConfig jsonRpcConfig,
    ISpecProvider specProvider,
    IBlockchainBridge blockchainBridge,
    IBlocksConfig blocksConfig,
    IBlockFinder blockFinder,
    IStateReader stateReader)
    : IDebugRpcModule
{
    private readonly ILogger _logger = logManager.GetClassLogger();
    private readonly BlockDecoder _blockDecoder = new();
    private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
    private readonly IStateReader _stateReader = stateReader;

    public ResultWrapper<IDictionary<string, object>> debug_state()
    {
        var header = blockchainBridge.HeadBlock?.Header;
        if (header is null)
        {
            return ResultWrapper<IDictionary<string, object>>.Fail("No head block", ErrorCodes.ResourceUnavailable);
        }

        var result = new Dictionary<string, object>();

        foreach (var address in WatchedAddresses)
        {
            ITrieStore _trieStore = (ITrieStore)_stateReader.GetType().GetField("_trieStore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(_stateReader);
            if (!_stateReader.TryGetAccount(header, address, out var account))
            {
                result[address.ToString()] = null;
                continue;
            }

            var storage = new Dictionary<string, string>();

            if (!account.IsStorageEmpty)
            {
                StorageTree storageTree = new(_trieStore.GetTrieStore(address), new Hash256(account.StorageRoot), logManager);
                StorageDumpVisitor storageVisitor = new(storage);
                storageTree.Accept(storageVisitor, new Hash256(account.StorageRoot), VisitingOptions.Default, new Hash256(account.StorageRoot), new Hash256(account.StorageRoot));
            }

            result[address.ToString()] = new
            {
                nonce = "0x" + account.Nonce.ToString("x"),
                balance = "0x" + account.Balance.ToString("x"),
                code = _stateReader.GetCode(account.CodeHash),
                storage
            };
        }

        return ResultWrapper<IDictionary<string, object>>.Success(result);
    }

    private static readonly Address[] WatchedAddresses = new[]
    {
        new Address("0xbabe2bed00000000000000000000000000000003"),
        new Address("0x00000961ef480eb55e80d19ad83579a64c007002"),
        new Address("0x0000bbddc7ce488642fb579f8b00f3a590007251"),
        new Address("0x0000f90827f1c53a10cb7a02335b175320002935"),
        new Address("0x000f3df6d732807ef1319fb7b8bb8522d0beac02"),
        new Address("0x2000000000000000000000000000000000000001"),
        new Address("0xfffffffffffffffffffffffffffffffffffffffe"),
        new Address("0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b"),
        new Address("0x1000000000000000000000000000000000001000"),
        new Address("0x1559000000000000000000000000000000000000")
    };

    private class StorageDumpVisitor(Dictionary<string, string> storage) : ITreeVisitor<OldStyleTrieVisitContext>
    {
        private readonly Dictionary<string, string> _storage = storage;

        public bool IsFullDbScan => true;

        public bool ShouldVisit(in OldStyleTrieVisitContext context, in ValueHash256 nextNode) => true;

        public void VisitTree(in OldStyleTrieVisitContext context, in ValueHash256 rootHash) { }

        public void VisitMissingNode(in OldStyleTrieVisitContext context, in ValueHash256 nodeHash) { }

        public void VisitBranch(in OldStyleTrieVisitContext context, TrieNode node) { }

        public void VisitExtension(in OldStyleTrieVisitContext context, TrieNode node) { }

        public void VisitLeaf(in OldStyleTrieVisitContext context, TrieNode node)
        {
            UInt256 key = new(node.Key, isBigEndian: true);
            Rlp.ValueDecoderContext decoder = new(node.Value.Span);
            byte[] valueBytes = decoder.DecodeByteArray();
            string valueHex = valueBytes.Length == 0 ? "0x" : "0x" + valueBytes.ToHexString();
            _storage["0x" + key.ToString("x")] = valueHex;
        }

        public void VisitAccount(in OldStyleTrieVisitContext context, TrieNode node, in AccountStruct account) { }
    }

    public class TreeDumper(bool expectAccounts = true) : ITreeVisitor<OldStyleTrieVisitContext>
    {
        private readonly StringBuilder _builder = new();
        public bool ExpectAccounts => expectAccounts;

        public void Reset()
        {
            _builder.Clear();
        }

        public bool IsFullDbScan { get; init; } = true;

        public bool ShouldVisit(in OldStyleTrieVisitContext _, in ValueHash256 nextNode)
        {
            return true;
        }

        public void VisitTree(in OldStyleTrieVisitContext context, in ValueHash256 rootHash)
        {
            if (rootHash == Keccak.EmptyTreeHash)
            {
                _builder.AppendLine("EMPTY TREE");
            }
            else
            {
                _builder.AppendLine(context.IsStorage ? "STORAGE TREE" : "STATE TREE");
            }
        }

        private static string GetPrefix(in OldStyleTrieVisitContext context) => string.Concat($"{GetIndent(context.Level)}", context.IsStorage ? "STORAGE " : string.Empty, $"{GetChildIndex(context)}");

        private static string GetIndent(int level) => new('+', level * 2);
        private static string GetChildIndex(in OldStyleTrieVisitContext context) => context.BranchChildIndex is null ? string.Empty : $"{context.BranchChildIndex:x2} ";

        public void VisitMissingNode(in OldStyleTrieVisitContext context, in ValueHash256 nodeHash)
        {
            _builder.AppendLine($"{GetIndent(context.Level)}{GetChildIndex(context)}MISSING {nodeHash}");
        }

        public void VisitBranch(in OldStyleTrieVisitContext context, TrieNode node)
        {
            _builder.AppendLine($"{GetPrefix(context)}BRANCH | -> {KeccakOrRlpStringOfNode(node)}");
        }

        public void VisitExtension(in OldStyleTrieVisitContext context, TrieNode node)
        {
            _builder.AppendLine($"{GetPrefix(context)}EXTENSION {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
        }

        public void VisitLeaf(in OldStyleTrieVisitContext context, TrieNode node)
        {
            if (!expectAccounts)
            {
                _builder.AppendLine($"{GetPrefix(context)}LEAF {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
            }
        }

        public void VisitAccount(in OldStyleTrieVisitContext context, TrieNode node, in AccountStruct account)
        {
            string leafDescription = context.IsStorage ? "LEAF " : "ACCOUNT ";
            _builder.AppendLine($"{GetPrefix(context)}{leafDescription} {Nibbles.FromBytes(node.Key).ToPackedByteArray().ToHexString(false)} -> {KeccakOrRlpStringOfNode(node)}");
            Rlp.ValueDecoderContext valueDecoderContext = new(node.Value.Span);
            if (!context.IsStorage)
            {
                _builder.AppendLine($"{GetPrefix(context)}  NONCE: {account.Nonce}");
                _builder.AppendLine($"{GetPrefix(context)}  BALANCE: {account.Balance}");
                _builder.AppendLine($"{GetPrefix(context)}  IS_CONTRACT: {account.IsContract}");
            }
            else
            {
                _builder.AppendLine($"{GetPrefix(context)}  VALUE: {valueDecoderContext.DecodeByteArray().ToHexString(true, true)}");
            }
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private static string? KeccakOrRlpStringOfNode(TrieNode node)
        {
            return node.Keccak is not null ? node.Keccak!.Bytes.ToHexString() : node.FullRlp.Span.ToHexString();
        }
    }

    public ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number)
    {
        ChainLevelInfo levelInfo = debugBridge.GetLevelInfo(number);
        return levelInfo is null
            ? ResultWrapper<ChainLevelForRpc>.Fail($"Chain level {number} does not exist", ErrorCodes.ResourceNotFound)
            : ResultWrapper<ChainLevelForRpc>.Success(new ChainLevelForRpc(levelInfo));
    }

    public ResultWrapper<int> debug_deleteChainSlice(in long startNumber, bool force = false)
    {
        return ResultWrapper<int>.Success(debugBridge.DeleteChainSlice(startNumber, force));
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Hash256 transactionHash, GethTraceOptions? options = null)
    {
        Hash256? blockHash = debugBridge.GetTransactionBlockHash(transactionHash);
        if (blockHash is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find block hash for transaction {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        var header = TryGetHeader<GethLikeTxTrace>(blockHash!, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        GethLikeTxTrace? transactionTrace = debugBridge.GetTransactionTrace(transactionHash, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {transactionHash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceCall(TransactionForRpc call, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        blockParameter ??= BlockParameter.Latest;

        var header = TryGetHeader<GethLikeTxTrace>(blockParameter, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        // default to previous block gas if unspecified
        call.Gas ??= header.GasLimit;

        // enforces gas cap
        call.EnsureDefaults(jsonRpcConfig.GasCap);

        Transaction tx = call.ToTransaction();
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        GethLikeTxTrace transactionTrace = debugBridge.GetTransactionTrace(tx, blockParameter, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace for hash: {tx.Hash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransaction)} request {tx.Hash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Hash256 blockhash, int index, GethTraceOptions options = null)
    {
        var header = TryGetHeader<GethLikeTxTrace>(blockhash, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var transactionTrace = debugBridge.GetTransactionTrace(blockhash, index, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockhash}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockhashAndIndex)} request {blockhash}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int index, GethTraceOptions options = null)
    {
        var header = TryGetHeader<GethLikeTxTrace>(blockParameter, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        long? blockNo = blockParameter.BlockNumber;
        if (!blockNo.HasValue)
        {
            throw new InvalidDataException("Block number value incorrect");
        }

        var transactionTrace = debugBridge.GetTransactionTrace(blockNo.Value, index, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Cannot find transactionTrace {blockNo}", ErrorCodes.ResourceNotFound);
        }

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceTransactionByBlockAndIndex)} request {blockNo}, result: trace");
        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Hash256 transactionHash, GethTraceOptions options = null)
    {
        var block = TryGetBlock<GethLikeTxTrace>(new Rlp(blockRlp), out var blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var transactionTrace = debugBridge.GetTransactionTrace(block, transactionHash, cancellationToken, options);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transactionTrace hash {transactionHash}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null)
    {
        var block = TryGetBlock<GethLikeTxTrace>(new Rlp(blockRlp), out var blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        var blockTrace = debugBridge.GetBlockTrace(block, cancellationToken, options);
        var transactionTrace = blockTrace?.ElementAtOrDefault(txIndex);
        if (transactionTrace is null)
        {
            return ResultWrapper<GethLikeTxTrace>.Fail($"Trace is null for RLP {blockRlp.ToHexString()} and transaction index {txIndex}", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<GethLikeTxTrace>.Success(transactionTrace);
    }

    public async Task<ResultWrapper<bool>> debug_migrateReceipts(long from, long to) =>
        ResultWrapper<bool>.Success(await debugBridge.MigrateReceipts(from, to));

    public Task<ResultWrapper<bool>> debug_insertReceipts(BlockParameter blockParameter, ReceiptForRpc[] receiptForRpc)
    {
        debugBridge.InsertReceipts(blockParameter, receiptForRpc.Select(static r => r.ToReceipt()).ToArray());
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null)
    {
        var block = TryGetBlock<IReadOnlyCollection<GethLikeTxTrace>>(new Rlp(blockRlp), out var blockError);
        if (blockError is not null)
        {
            return blockError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        try
        {
            var blockTrace = debugBridge.GetBlockTrace(block, cancellationToken, options);

            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for RLP {blockRlp.ToHexString()}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlock)} request {blockRlp.ToHexString()}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (RlpException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Error decoding block RLP: {blockRlp.ToHexString()}", ErrorCodes.InvalidInput);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Couldn't find any block", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByNumber(BlockParameter blockNumber, GethTraceOptions options = null)
    {
        var header = TryGetHeader<IReadOnlyCollection<GethLikeTxTrace>>(blockNumber, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(blockNumber, cancellationToken, options);

        try
        {
            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByNumber)} request {blockNumber}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockNumber}", ErrorCodes.InvalidInput);
        }
    }

    public ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>> debug_traceBlockByHash(Hash256 blockHash, GethTraceOptions options = null)
    {
        var header = TryGetHeader<IReadOnlyCollection<GethLikeTxTrace>>(blockHash, out var headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        using CancellationTokenSource? timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;
        IReadOnlyCollection<GethLikeTxTrace>? blockTrace = debugBridge.GetBlockTrace(new BlockParameter(blockHash), cancellationToken, options);

        try
        {
            if (blockTrace is null)
                return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.ResourceNotFound);

            if (_logger.IsTrace) _logger.Trace($"{nameof(debug_traceBlockByHash)} request {blockHash}, result: {blockTrace}");

            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Success(blockTrace);
        }
        catch (ArgumentNullException)
        {
            return ResultWrapper<IReadOnlyCollection<GethLikeTxTrace>>.Fail($"Trace is null for block {blockHash}", ErrorCodes.InvalidInput);
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
        byte[] rlp = debugBridge.GetBlockRlp(new BlockParameter(blockNumber));
        if (rlp is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockNumber} was not found", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<byte[]>.Success(rlp);
    }

    public ResultWrapper<byte[]> debug_getBlockRlpByHash(Hash256 hash)
    {
        byte[] rlp = debugBridge.GetBlockRlp(new BlockParameter(hash));
        if (rlp is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {hash} was not found", ErrorCodes.ResourceNotFound);
        }

        return ResultWrapper<byte[]>.Success(rlp);
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
        var dbValue = debugBridge.GetDbValue(dbName, key);
        return ResultWrapper<byte[]>.Success(dbValue);
    }

    public ResultWrapper<object> debug_getConfigValue(string category, string name)
    {
        var configValue = debugBridge.GetConfigValue(category, name);
        return ResultWrapper<object>.Success(configValue);
    }

    public ResultWrapper<bool> debug_resetHead(Hash256 blockHash)
    {
        debugBridge.UpdateHeadBlock(blockHash);
        return ResultWrapper<bool>.Success(true);
    }

    public ResultWrapper<string?> debug_getRawTransaction(Hash256 transactionHash)
    {
        Transaction? transaction = debugBridge.GetTransactionFromHash(transactionHash);
        if (transaction is null)
        {
            return ResultWrapper<string?>.Fail($"Transaction {transactionHash} was not found", ErrorCodes.ResourceNotFound);
        }

        RlpBehaviors encodingSettings = RlpBehaviors.SkipTypedWrapping | (transaction.NetworkWrapper is not null ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);

        using NettyRlpStream stream = TxDecoder.Instance.EncodeToNewNettyStream(transaction, encodingSettings);
        return ResultWrapper<string?>.Success(stream.AsSpan().ToHexString(true));
    }

    public ResultWrapper<byte[][]> debug_getRawReceipts(BlockParameter blockParameter)
    {
        TxReceipt[] receipts = debugBridge.GetReceiptsForBlock(blockParameter);
        if (receipts is null)
        {
            return ResultWrapper<byte[][]>.Fail($"Receipts are not found for block {blockParameter}", ErrorCodes.ResourceNotFound);
        }

        if (receipts.Length == 0)
        {
            return ResultWrapper<byte[][]>.Success([]);
        }
        RlpBehaviors behavior =
            (specProvider.GetReceiptSpec(receipts[0].BlockNumber).IsEip658Enabled ?
                RlpBehaviors.Eip658Receipts : RlpBehaviors.None) | RlpBehaviors.SkipTypedWrapping;
        var rlp = receipts.Select(tx => Rlp.Encode(tx, behavior).Bytes);
        return ResultWrapper<byte[][]>.Success(rlp.ToArray());
    }

    public ResultWrapper<byte[]> debug_getRawBlock(BlockParameter blockParameter)
    {
        var blockRLP = debugBridge.GetBlockRlp(blockParameter);
        if (blockRLP is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
        }
        return ResultWrapper<byte[]>.Success(blockRLP);
    }

    public ResultWrapper<byte[]> debug_getRawHeader(BlockParameter blockParameter)
    {
        var block = debugBridge.GetBlock(blockParameter);
        if (block is null)
        {
            return ResultWrapper<byte[]>.Fail($"Block {blockParameter} was not found", ErrorCodes.ResourceNotFound);
        }
        Rlp rlp = Rlp.Encode<BlockHeader>(block.Header);
        return ResultWrapper<byte[]>.Success(rlp.Bytes);
    }

    public Task<ResultWrapper<SyncReportSummary>> debug_getSyncStage()
    {
        return ResultWrapper<SyncReportSummary>.Success(debugBridge.GetCurrentSyncStage());
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IEnumerable<string>? files = debugBridge.TraceBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<string>> debug_standardTraceBadBlockToFile(Hash256 blockHash, GethTraceOptions options = null)
    {
        using CancellationTokenSource cancellationTokenSource = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        IEnumerable<string>? files = debugBridge.TraceBadBlockToFile(blockHash, cancellationToken, options);

        if (_logger.IsTrace) _logger.Trace($"{nameof(debug_standardTraceBadBlockToFile)} request {blockHash}, result: {files}");

        return ResultWrapper<IEnumerable<string>>.Success(files);
    }

    public ResultWrapper<IEnumerable<BadBlock>> debug_getBadBlocks()
    {
        IEnumerable<BadBlock> badBlocks = debugBridge.GetBadBlocks().Select(block => new BadBlock(block, true, specProvider, _blockDecoder));
        return ResultWrapper<IEnumerable<BadBlock>>.Success(badBlocks);
    }

    private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
        jsonRpcConfig.BuildTimeoutCancellationToken();

    public ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> debug_simulateV1(
        SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        return new SimulateTxExecutor<GethLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, new GethStyleSimulateBlockTracerFactory(options: options ?? GethTraceOptions.Default), _secondsPerSlot)
            .Execute(payload, blockParameter);
    }

    public ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> debug_traceCallMany(TransactionBundle[] bundles, BlockParameter? blockParameter = null, GethTraceOptions? options = null)
    {
        if (bundles is null)
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail("Bundles array cannot be null", ErrorCodes.InvalidParams);

        if (bundles.Length == 0)
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(Array.Empty<GethLikeTxTrace[]>());

        for (int i = 0; i < bundles.Length; i++)
        {
            if (bundles[i]?.Transactions is null)
                return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail($"Bundle at index {i} has null transactions", ErrorCodes.InvalidParams);
        }

        blockParameter ??= BlockParameter.Latest;

        BlockHeader header = TryGetHeader(blockParameter, out ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>? headerError);
        if (headerError is not null)
        {
            return headerError;
        }

        return bundles.Any(b => b.BlockOverride is not null || b.StateOverrides is not null)
            ? TraceCallManyWithOverrides(bundles, options, header)
            : TraceCallMany(bundles, blockParameter, options, header);
    }

    private ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> TraceCallMany(TransactionBundle[] bundles, BlockParameter blockParameter, GethTraceOptions? options, BlockHeader header)
    {
        PrepareTransactions(bundles, header);

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
        CancellationToken cancellationToken = timeout.Token;

        IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces = debugBridge
            .GetBundleTraces(bundles, blockParameter, cancellationToken, options);

        if (_logger.IsTrace)
        {
            int totalTransactions = bundles.Sum(b => b.Transactions?.Length ?? 0);
            _logger.Trace($"{nameof(debug_traceCallMany)} completed: {bundles.Length} bundles, {totalTransactions} transactions via simple path");
        }

        return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(bundleTraces);
    }

    private ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>> TraceCallManyWithOverrides(TransactionBundle[] bundles, GethTraceOptions? options, BlockHeader header)
    {
        PrepareTransactions(bundles, header);
        var simulatePayload = new SimulatePayload<TransactionForRpc>
        {
            BlockStateCalls = bundles.Select(bundle => new BlockStateCall<TransactionForRpc>
            {
                BlockOverrides = bundle.BlockOverride,
                StateOverrides = bundle.StateOverrides,
                Calls = bundle.Transactions
            }).ToList()
        };

        BlockParameter concreteBlockParameter = new(header.Number);

        using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();

        ResultWrapper<IReadOnlyList<SimulateBlockResult<GethLikeTxTrace>>> simulationResult =
            new SimulateTxExecutor<GethLikeTxTrace>(
                blockchainBridge,
                blockFinder,
                jsonRpcConfig,
                new GethStyleSimulateBlockTracerFactory(options: options ?? GethTraceOptions.Default),
                _secondsPerSlot
            ).Execute(simulatePayload, concreteBlockParameter);

        if (simulationResult.ErrorCode != 0)
        {
            string errorMessage = simulationResult.Result ? $"Simulation failed with error code {simulationResult.ErrorCode}." : simulationResult.Result.ToString();
            if (_logger.IsWarn) _logger.Warn($"debug_traceCallMany simulation failed: Code={simulationResult.ErrorCode}, Details={errorMessage}");
            return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Fail(errorMessage, simulationResult.ErrorCode);
        }

        IEnumerable<IEnumerable<GethLikeTxTrace>> bundleTraces = simulationResult.Data.Select(blockResult => blockResult.Traces);

        return ResultWrapper<IEnumerable<IEnumerable<GethLikeTxTrace>>>.Success(bundleTraces);
    }

    private void PrepareTransactions(TransactionBundle[] bundles, BlockHeader header)
    {
        foreach (TransactionBundle bundle in bundles)
        {
            foreach (TransactionForRpc call in bundle.Transactions)
            {
                call.Gas ??= header.GasLimit;
                call.EnsureDefaults(jsonRpcConfig.GasCap);
            }
        }
    }

    private static ResultWrapper<TResult> GetFailureResult<TResult, TSearch>(SearchResult<TSearch> searchResult, bool isTemporary)
        where TSearch : class =>
        ResultWrapper<TResult>.Fail(searchResult, isTemporary && searchResult.ErrorCode == ErrorCodes.ResourceNotFound);

    private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
        ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

    private static ResultWrapper<TResult> GetRlpDecodingFailureResult<TResult>(Rlp blockRlp) =>
        ResultWrapper<TResult>.Fail($"Error decoding block RLP: {blockRlp.Bytes.ToHexString()}", ErrorCodes.InvalidInput);

    private BlockHeader? TryGetHeader<TResult>(BlockParameter blockParameter, out ResultWrapper<TResult>? error)
    {
        SearchResult<BlockHeader> searchResult = blockFinder.SearchForHeader(blockParameter);
        BlockHeader? header = searchResult.Object;

        if (searchResult.IsError)
        {
            error = GetFailureResult<TResult, BlockHeader>(searchResult, debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        if (!blockchainBridge.HasStateForBlock(header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        error = default!;
        return header;
    }

    private BlockHeader? TryGetHeader<TResult>(Hash256 blockHash, out ResultWrapper<TResult>? error)
    {
        BlockHeader? header = blockFinder.FindHeader(blockHash);

        if (header is null)
        {
            error = GetFailureResult<TResult, BlockHeader>(
                new SearchResult<BlockHeader>($"Cannot find header for block hash: {blockHash}", ErrorCodes.ResourceNotFound),
                debugBridge.HaveNotSyncedHeadersYet());
            return null;
        }
        if (!blockchainBridge.HasStateForBlock(header))
        {
            error = GetStateFailureResult<TResult>(header);
            return null;
        }

        error = default!;
        return header;
    }

    private Block? TryGetBlock<TResult>(Rlp blockRlp, out ResultWrapper<TResult>? error)
    {
        Block? block;

        try
        {
            block = _blockDecoder.Decode(blockRlp.Bytes);
            if (block is null)
            {
                error = GetRlpDecodingFailureResult<TResult>(blockRlp);
                return null;
            }

            if (block.TotalDifficulty is null)
            {
                block.Header.TotalDifficulty = 1;
            }
        }
        catch (RlpException)
        {
            error = GetRlpDecodingFailureResult<TResult>(blockRlp);
            return null;
        }

        if (!blockchainBridge.HasStateForBlock(block.Header))
        {
            error = GetStateFailureResult<TResult>(block.Header);
            return null;
        }

        error = default!;
        return block;
    }
}
