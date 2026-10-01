// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FastEnumUtility;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Messages;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.State.OverridableEnv;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.TxPool;

namespace Nethermind.JsonRpc.Modules.Trace
{
    /// <summary>
    /// All methods that receive a call from users uses ITransactionProcessor.Trace
    /// A call priced at zero pays no gas fee and runs with a zero base fee, as eth_call does; a priced one is charged for gas
    /// A signed transaction from users is validated with block inclusion's checks in the latest block's context and uses ITransactionProcessor.Execute
    ///
    /// All methods that traces transactions from chain uses ITransactionProcessor.Execute
    /// From-chain transactions should have stateDiff as we got during normal execution. Also we are sure that sender have enough funds to pay gas
    /// </summary>
    public class TraceRpcModule(
        IReceiptFinder receiptFinder,
        IOverridableEnv<ITracer> tracerEnv,
        IBlockFinder blockFinder,
        IJsonRpcConfig jsonRpcConfig,
        IBlockchainBridge blockchainBridge,
        ISpecProvider specProvider,
        ITxValidator txValidator,
        IBlocksConfig blocksConfig,
        IPrefixStateSeedSource prefixSeeds,
        ILogManager logManager,
        IParallelBlockTracer? parallelTracer = null)
        : ITraceRpcModule
    {
        private readonly TxDecoder _txDecoder = TxDecoder.Instance;
        private readonly EthereumEcdsa _ecdsa = new(specProvider.ChainId);
        private readonly ulong _secondsPerSlot = blocksConfig.SecondsPerSlot;
        private readonly ILogger _logger = logManager.GetClassLogger<TraceRpcModule>();

        /// <summary>
        /// Parses Parity trace type names (case-insensitive) into a combined <see cref="ParityTraceTypes"/> flag set.
        /// </summary>
        /// <returns><see langword="false"/> when <paramref name="types"/> is <see langword="null"/> or contains an unknown name.</returns>
        public static bool TryGetParityTypes(string[]? types, out ParityTraceTypes result)
        {
            result = ParityTraceTypes.None;
            // A JSON null per-call selection in trace_callMany reaches here.
            if (types is null) return false;
            foreach (string type in types)
            {
                if (!FastEnum.TryParse(type, ignoreCase: true, out ParityTraceTypes parsed))
                {
                    result = ParityTraceTypes.None;
                    return false;
                }
                result |= parsed;
            }
            return true;
        }

        public static ResultWrapper<T> InvalidTraceTypes<T>() => ResultWrapper<T>.Fail("Invalid trace types", ErrorCodes.InvalidParams);

        /// <summary>
        /// Whether <paramref name="blockParameter"/> is the pending tag, which the trace methods reject.
        /// </summary>
        /// <remarks>
        /// No pending block is built for RPC, so the pending tag resolves to the head block, and a trace at pending
        /// would silently be a trace at latest.
        /// </remarks>
        public static bool IsPending(BlockParameter? blockParameter) => blockParameter?.Type == BlockParameterType.Pending;

        /// <summary>
        /// The invalid-params failure returned for a pending block tag; see <see cref="IsPending"/>.
        /// </summary>
        public static ResultWrapper<T> PendingNotSupported<T>() => ResultWrapper<T>.Fail("Pending block is not supported for tracing", ErrorCodes.InvalidParams);

        /// <summary>
        /// Traces one transaction. A call priced at zero runs with a zero base fee and pays no gas fee; a priced call is charged for gas.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_call(TransactionForRpc call, string[] traceTypes, BlockParameter? blockParameter = null, Dictionary<Address, AccountOverride>? stateOverride = null)
        {
            if (GetOtherChainError(call) is { } chainError)
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(chainError, ErrorCodes.InvalidParams);

            blockParameter ??= BlockParameter.Latest;
            if (IsPending(blockParameter))
                return PendingNotSupported<ParityTxTraceFromReplay>();

            SearchResult<BlockHeader> headerSearch = blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);

            Result<Transaction> txResult = ToCallTransaction(call, headerSearch.Object!);
            return !txResult.Success(out Transaction? transaction, out string? error)
                ? ResultWrapper<ParityTxTraceFromReplay>.Fail(error, ErrorCodes.InvalidInput)
                : TraceTx(transaction, traceTypes, headerSearch, stateOverride);
        }

        /// <summary>
        /// Traces list of transactions, each on the state the previous ones leave. Each call priced at zero runs with a
        /// zero base fee and pays no gas fee; a priced call sees the block's base fee and is charged for gas.
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_callMany(TraceCallManyRequest request, BlockParameter? blockParameter = null)
        {
            using TraceCallManyRequest _ = request;
            ArrayPoolList<TransactionForRpcWithTraceTypes> calls = request.Calls;
            foreach (TransactionForRpcWithTraceTypes call in calls)
            {
                if (GetOtherChainError(call.Transaction) is { } chainError)
                    return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(chainError, ErrorCodes.InvalidParams);
            }

            blockParameter ??= BlockParameter.Latest;
            if (IsPending(blockParameter))
            {
                return PendingNotSupported<IEnumerable<ParityTxTraceFromReplay>>();
            }

            SearchResult<BlockHeader> headerSearch = blockFinder.SearchForHeader(blockParameter);
            if (headerSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!;
            if (!blockchainBridge.HasStateForBlock(header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(header);
            }

            Dictionary<Hash256, ParityTraceTypes> traceTypeByTransaction = [with(calls.Count)];
            Transaction[] txs = new Transaction[calls.Count];
            for (int i = 0; i < calls.Count; i++)
            {
                Result<Transaction> txResult = ToCallTransaction(calls[i].Transaction, header);
                if (!txResult.Success(out Transaction? tx, out string? error))
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(error, ErrorCodes.InvalidInput);
                }

                tx.Hash = new Hash256(new UInt256((ulong)i).ToValueHash());
                if (!TryGetParityTypes(calls[i].TraceTypes, out ParityTraceTypes traceTypes))
                {
                    return InvalidTraceTypes<IEnumerable<ParityTxTraceFromReplay>>();
                }

                txs[i] = tx;
                traceTypeByTransaction.Add(tx.Hash, traceTypes);
            }

            Block block = new(header, new BlockBody(txs, []));

            return BuildStreamingMultiResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        traceTypeByTransaction, ParityTraceTypes.None,
                        ParityTraceStreamMode.Replay, includeTxHash: false,
                        writer, pipeWriter, ct, specProvider: specProvider);
                    TraceBlockStreaming(block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> traces = TraceBlock(block, new(traceTypeByTransaction, specProvider));
                    return traces.Select(static t => new ParityTxTraceFromReplay(t));
                });
        }

        /// <summary>
        /// Converts <paramref name="call"/> for trace_call and trace_callMany on top of <paramref name="header"/> as eth_call does:
        /// it rejects a priority fee above the fee cap, and gives a blob call without a positive blob fee cap a zero cap, with
        /// which <see cref="UnpricedCallTraceAdapter"/> runs it at a zero blob base fee.
        /// </summary>
        private Result<Transaction> ToCallTransaction(TransactionForRpc call, BlockHeader header)
        {
            IReleaseSpec spec = specProvider.GetSpec(header);
            Result<Transaction> result = BlobTransactionForRpc.WithZeroBlobFeeCapOmitted(call).ToValidatedTransaction(gasCap: jsonRpcConfig.GasCap, spec: spec);
            if (!result.Success(out Transaction? tx, out _))
                return result;

            if (tx.GetTipAboveFeeCapError(spec) is { } tipAboveFeeCap)
                return Result<Transaction>.Fail(tipAboveFeeCap);

            if (tx.CarriesBlobs)
                tx.MaxFeePerBlobGas ??= UInt256.Zero;

            return result;
        }

        /// <summary>
        /// Returns an error message when <paramref name="call"/> sets a <c>chainId</c> for another chain, otherwise <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// A <c>chainId</c> for another chain is invalid regardless of state, so it is checked before the block lookup and the other call checks.
        /// </remarks>
        private string? GetOtherChainError(TransactionForRpc call)
        {
            ulong chainId = blockchainBridge.GetChainId();
            return call is LegacyTransactionForRpc { ChainId: { } requestedChainId } && requestedChainId != chainId
                ? RpcTransactionErrors.InvalidChainId(chainId, requestedChainId)
                : null;
        }

        /// <summary>
        /// Returns an error message when eth_sendRawTransaction's signature and chain-id checks at <paramref name="spec"/> reject
        /// signed <paramref name="tx"/>, otherwise <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Execution doesn't reject a transaction for its chain: sender recovery hashes a typed transaction with its own chain id,
        /// recovering the real sender, and, where the spec validates chain ids, a legacy one signed for another chain as
        /// pre-EIP-155, recovering an unrelated address.
        /// A legacy transaction's chain id is in its signature <c>v</c>, so the pool's <see cref="LegacySignatureTxValidator"/>
        /// decides it and, as in eth_sendRawTransaction, accepts a pre-EIP-155 signature. A rejection names the chain when
        /// <c>v</c> is for another one and the signature is otherwise valid.
        /// </remarks>
        private string? GetSignatureError(Transaction tx, IReleaseSpec spec)
        {
            ulong chainId = blockchainBridge.GetChainId();
            if (tx.Type != TxType.Legacy)
            {
                // A frame transaction has no envelope signature.
                string? signatureError = tx.Signature is null ? null : SignatureTxValidator.Instance.IsWellFormed(tx, spec).Error;
                return signatureError
                    ?? (tx.ChainId != chainId ? TxErrorMessages.InvalidTxChainId(chainId, tx.ChainId) : null);
            }

            ValidationResult result = new LegacySignatureTxValidator(chainId).IsWellFormed(tx, spec);
            return result ? null
                : tx.Signature?.ChainId is { } signedChainId && signedChainId != chainId
                    && new LegacySignatureTxValidator(signedChainId).IsWellFormed(tx, spec)
                    ? TxErrorMessages.InvalidTxChainId(chainId, signedChainId)
                : result.Error;
        }

        /// <summary>
        /// Returns an error message when signed <paramref name="tx"/> is invalid in the context of <paramref name="header"/>
        /// regardless of state, otherwise <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// After the signature and chain-id checks of <see cref="GetSignatureError"/>, applies the node's transaction validator,
        /// as block validation does to each transaction: among others, the fork must enable the transaction type, and the gas
        /// limit, fee and type-specific fields must be valid. As in block validation, the sender is recovered first under
        /// EIP-2780, whose intrinsic gas depends on it. The checks that depend on state run when the transaction is executed.
        /// </remarks>
        private string? GetSignedTransactionError(Transaction tx, BlockHeader header)
        {
            IReleaseSpec spec = specProvider.GetSpec(header);
            if (GetSignatureError(tx, spec) is { } signatureError)
                return signatureError;

            if (spec.IsEip2780Enabled && tx.SenderAddress is null && tx.Signature is not null)
                tx.SenderAddress = _ecdsa.RecoverAddress(tx, !spec.ValidateChainId);

            return txValidator.IsWellFormed(tx, spec, header.GasLimit).Error;
        }

        /// <summary>
        /// Traces one raw transaction on the latest state, in the latest block's context, validated there with block inclusion's
        /// checks: an invalid transaction, for example one whose nonce isn't the sender's, is rejected, and a valid one runs as signed.
        /// The context is the latest block, where the transaction is traced, not the next block, where it would be included.
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay> trace_rawTransaction(byte[] data, string[] traceTypes)
        {
            try
            {
                RlpReader ctx = new(data);
                Transaction tx = _txDecoder.DecodeCompleteNotNull(ref ctx, RlpBehaviors.SkipTypedWrapping);
                ulong gasCap = jsonRpcConfig.GasCap.EffectiveGasCap();
                if (tx.GasLimit > gasCap)
                {
                    return ResultWrapper<ParityTxTraceFromReplay>.Fail(
                        $"Signed transaction gas limit exceeds the RPC gas cap of {gasCap}.",
                        ErrorCodes.ClientLimitExceededError);
                }
                return TraceTx(tx, traceTypes, blockFinder.SearchForHeader(BlockParameter.Latest), isSigned: true);
            }
            catch (RlpException)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail("Invalid RLP.", ErrorCodes.TransactionRejected);
            }
        }

        private ResultWrapper<ParityTxTraceFromReplay> TraceTx(Transaction tx, string[] traceTypes, SearchResult<BlockHeader> headerSearch,
            Dictionary<Address, AccountOverride>? stateOverride = null, bool isSigned = false)
        {
            if (!TryGetParityTypes(traceTypes, out ParityTraceTypes parityTypes))
            {
                return InvalidTraceTypes<ParityTxTraceFromReplay>();
            }

            if (headerSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(headerSearch);
            }

            BlockHeader header = headerSearch.Object!.Clone();
            if (isSigned && GetSignedTransactionError(tx, header) is { } signedTransactionError)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Fail(signedTransactionError, ErrorCodes.TransactionRejected);
            }

            Block block = new(header, [tx], []);

            return BuildStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        parityTypes, ParityTraceStreamMode.Replay, includeTxHash: false,
                        writer, pipeWriter, ct, specProvider: specProvider);
                    using Scope<ITracer> env = tracerEnv.BuildAndOverride(header, stateOverride);
                    TraceConstructedBlock(env.Component, block, streamingTracer.WithCancellation(ct), isSigned);
                },
                runBuffered: () =>
                {
                    using Scope<ITracer> env = tracerEnv.BuildAndOverride(header, stateOverride);
                    IReadOnlyCollection<ParityLikeTxTrace> result = TraceBlockDirect(env.Component, block, new(parityTypes, specProvider), isSigned);
                    return new ParityTxTraceFromReplay(result.SingleOrDefault());
                });
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<ParityTxTraceFromReplay?> trace_replayTransaction(Hash256 txHash, string[] traceTypes, bool traceNonCanonical = false)
        {
            if (!TryGetParityTypes(traceTypes, out ParityTraceTypes parityTypes))
            {
                return InvalidTraceTypes<ParityTxTraceFromReplay?>();
            }

            Hash256? blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return ResultWrapper<ParityTxTraceFromReplay?>.Success(null);
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(new BlockParameter(blockHash, requireCanonical: !traceNonCanonical));
            if (blockSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay?>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<ParityTxTraceFromReplay?>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<ParityTxTraceFromReplay?>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingSingleResult(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        txHash, parityTypes, ParityTraceStreamMode.Replay, includeTxHash: true,
                        writer, pipeWriter, ct, specProvider: specProvider);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, transactionHash: txHash);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new ParityLikeBlockTracer(txHash, parityTypes, specProvider), transactionHash: txHash);
                    return new ParityTxTraceFromReplay(txTrace, includeTransactionHash: true);
                });
        }

        /// <summary>
        /// Traces one block. As it replays existing block will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromReplay>> trace_replayBlockTransactions(BlockParameter blockParameter, string[] traceTypes)
        {
            if (IsPending(blockParameter))
            {
                return PendingNotSupported<IEnumerable<ParityTxTraceFromReplay>>();
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            if (!TryGetParityTypes(traceTypes, out ParityTraceTypes traceTypes1))
            {
                return InvalidTraceTypes<IEnumerable<ParityTxTraceFromReplay>>();
            }

            // Genesis has no parent to replay from, and no transactions or rewards to trace.
            if (block.IsGenesis)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Success([]);
            }

            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromReplay>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromReplay>>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingMultiResult<ParityTxTraceFromReplay>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        traceTypes1, ParityTraceStreamMode.Replay, includeTxHash: true,
                        writer, pipeWriter, ct, specProvider: specProvider);
                    if (!TryStreamBlockInParallel(parentHeader, block, traceTypes1, streamingTracer, ct))
                        ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlockParallelOrReplay(parentHeader, block, traceTypes1);
                    return txTraces.Select(static t => new ParityTxTraceFromReplay(t, true));
                });
        }

        /// <summary>
        /// Traces blocks specified in filter. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_filter(TraceFilterForRpc traceFilterForRpc)
        {
            CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            bool ownsTimeout = true;
            try
            {
                (BlockParameter fromBlock, BlockParameter toBlock) = traceFilterForRpc.GetBlockRange();
                if (IsPending(fromBlock) || IsPending(toBlock))
                {
                    return PendingNotSupported<IEnumerable<ParityTxTraceFromStore>>();
                }

                if (blockFinder.IsRangeInFuture(fromBlock, toBlock))
                {
                    return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(BlockFinderExtensions.BlockRangeInFuture, ErrorCodes.InvalidParams);
                }

                // Collect the whole range first so search errors (e.g. from > to) take precedence over state checks.
                List<(Block Block, BlockHeader? Parent)> blocks = [];
                foreach (SearchResult<Block> blockSearch in blockFinder.SearchForBlocksOnMainChain(fromBlock, toBlock))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (blockSearch.IsError)
                    {
                        return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
                    }
                    Block block = blockSearch.Object!;
                    if (!block.IsGenesis) blocks.Add((block, null));
                }

                BlockHeader? previous = null;
                for (int i = 0; i < blocks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Block block = blocks[i].Block;
                    if (!blockchainBridge.HasStateForBlock(block.Header))
                    {
                        return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    BlockHeader parentHeader;
                    if (previous is not null && previous.Hash == block.Header.ParentHash)
                    {
                        parentHeader = previous;
                    }
                    else
                    {
                        SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
                        if (parentSearch.IsError)
                        {
                            return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
                        }
                        parentHeader = parentSearch.Object!;
                        if (!blockchainBridge.HasStateForBlock(parentHeader))
                        {
                            return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentHeader);
                        }
                    }

                    blocks[i] = (block, parentHeader);
                    previous = block.Header;
                }

                ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
                TxTraceFilter filter = new(traceFilterForRpc.FromAddress, traceFilterForRpc.ToAddress, traceFilterForRpc.After ?? 0, traceFilterForRpc.Count, traceFilterForRpc.Mode);

                cancellationToken.ThrowIfCancellationRequested();
                ownsTimeout = false;
                return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                    runStreaming: (writer, pipeWriter, ct) =>
                    {
                        using StreamingParityLikeBlockTracer streamingTracer = new(
                            types, ParityTraceStreamMode.Store, includeTxHash: false,
                            writer, pipeWriter, ct, storeFilter: filter, specProvider: specProvider);
                        foreach ((Block block, BlockHeader? parentHeader) in blocks)
                        {
                            if (filter.IsExhausted) break;
                            if (!TryStreamBlockInParallel(parentHeader!, block, types, streamingTracer, ct))
                                ExecuteBlockStreaming(parentHeader!, block, streamingTracer, ct);
                        }
                    },
                    runBuffered: () => RunBufferedTraceFilter(blocks, filter, cancellationToken),
                    timeoutCts: timeout);
            }
            finally
            {
                if (ownsTimeout) timeout.Dispose();
            }
        }

        private IEnumerable<ParityTxTraceFromStore> RunBufferedTraceFilter(List<(Block Block, BlockHeader? Parent)> blocks, TxTraceFilter filter, CancellationToken cancellationToken)
        {
            ArrayPoolList<ParityTxTraceFromStore> result = [with(blocks.Count)];
            try
            {
                foreach ((Block block, BlockHeader? parentHeader) in blocks)
                {
                    if (filter.IsExhausted) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = ExecuteBlockParallelOrReplay(parentHeader!, block, ParityTraceTypes.Trace | ParityTraceTypes.Rewards, cancellationToken);
                    result.AddRange(filter.FilterTxTraces(txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace)));
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }

            return result;
        }

        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>> trace_block(BlockParameter blockParameter, string? fork = null)
        {
            if (IsPending(blockParameter))
            {
                return PendingNotSupported<IEnumerable<ParityTxTraceFromStore>>();
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(blockParameter);
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;

            if (!TryResolveForkSpec(fork, out IReleaseSpec? forkSpec, out ResultWrapper<IEnumerable<ParityTxTraceFromStore>>? forkError))
                return forkError!;

            // Genesis has no parent to replay from, and no transactions or rewards to trace.
            if (block.IsGenesis)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Success([]);
            }

            if (!blockchainBridge.HasStateForBlock(block.Header))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(block.Header);
            }
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;
            ParityTraceTypes types = ParityTraceTypes.Trace | ParityTraceTypes.Rewards;
            ISpecProvider tracerSpecs = forkSpec is null ? specProvider : new SingleReleaseSpecProvider(forkSpec, specProvider.NetworkId, specProvider.ChainId);

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        types, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct, specProvider: tracerSpecs);
                    if (forkSpec is not null || !TryStreamBlockInParallel(parentHeader, block, types, streamingTracer, ct))
                        ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, forkSpec);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTraces = forkSpec is null
                        ? ExecuteBlockParallelOrReplay(parentHeader, block, types)
                        : ExecuteBlock(parentHeader, block, new(types, tracerSpecs), forkSpec);
                    return txTraces.SelectMany(ParityTxTraceFromStore.FromTxTrace);
                });
        }

        /// <summary>
        /// Traces one transaction and returns its trace at <paramref name="traceAddress"/>. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<ParityTxTraceFromStore?> trace_get(Hash256 txHash, long[] traceAddress) =>
            SelectTraceAddress(trace_transaction(txHash), traceAddress);

        /// <summary>
        /// Selects the trace whose <c>traceAddress</c> equals <paramref name="traceAddress"/>: an empty path is the root,
        /// <c>[0]</c> its first child. Returns <see langword="null"/> when the transaction is not found or has no trace at that path,
        /// and passes a failure through.
        /// </summary>
        public static ResultWrapper<ParityTxTraceFromStore?> SelectTraceAddress(ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> traceTransaction, long[] traceAddress)
        {
            using (traceTransaction)
            {
                if (!traceTransaction.Result)
                {
                    return ResultWrapper<ParityTxTraceFromStore?>.Fail(traceTransaction.Result.Error!, traceTransaction.ErrorCode, traceTransaction.IsTemporary);
                }

                foreach (ParityTxTraceFromStore trace in traceTransaction.Data ?? [])
                {
                    if (HasTraceAddress(trace, traceAddress))
                    {
                        return ResultWrapper<ParityTxTraceFromStore?>.Success(trace);
                    }
                }

                return ResultWrapper<ParityTxTraceFromStore?>.Success(null);
            }

            static bool HasTraceAddress(ParityTxTraceFromStore trace, long[] traceAddress)
            {
                ReadOnlySpan<int> actual = trace.TraceAddress;
                if (actual.Length != traceAddress.Length) return false;
                for (int i = 0; i < actual.Length; i++)
                {
                    if (actual[i] != traceAddress[i]) return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Traces one transaction. As it replays existing transaction will charge gas
        /// </summary>
        public ResultWrapper<IEnumerable<ParityTxTraceFromStore>?> trace_transaction(Hash256 txHash, bool traceNonCanonical = false)
        {
            Hash256? blockHash = receiptFinder.FindBlockHash(txHash);
            if (blockHash is null)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Success(null);
            }

            SearchResult<Block> blockSearch = blockFinder.SearchForBlock(new BlockParameter(blockHash, requireCanonical: !traceNonCanonical));
            if (blockSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Fail(blockSearch);
            }

            Block block = blockSearch.Object!;
            SearchResult<BlockHeader> parentSearch = blockFinder.SearchForHeader(new BlockParameter(block.Header.ParentHash));
            if (parentSearch.IsError)
            {
                return ResultWrapper<IEnumerable<ParityTxTraceFromStore>?>.Fail(parentSearch);
            }

            if (!blockchainBridge.HasStateForBlock(parentSearch.Object))
            {
                return GetStateFailureResult<IEnumerable<ParityTxTraceFromStore>?>(parentSearch.Object);
            }

            BlockHeader parentHeader = parentSearch.Object!;

            return BuildStreamingMultiResult<ParityTxTraceFromStore>(
                runStreaming: (writer, pipeWriter, ct) =>
                {
                    using StreamingParityLikeBlockTracer streamingTracer = new(
                        txHash, ParityTraceTypes.Trace, ParityTraceStreamMode.Store, includeTxHash: false,
                        writer, pipeWriter, ct, specProvider: specProvider);
                    ExecuteBlockStreaming(parentHeader, block, streamingTracer, ct, transactionHash: txHash);
                },
                runBuffered: () =>
                {
                    IReadOnlyCollection<ParityLikeTxTrace> txTrace = ExecuteBlock(parentHeader, block, new(txHash, ParityTraceTypes.Trace, specProvider), transactionHash: txHash);
                    return ParityTxTraceFromStore.FromTxTrace(txTrace);
                });
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlock(Block block, ParityLikeBlockTracer tracer)
        {
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(block.Header);
            ITracer tracer2 = env.Component;

            return TraceBlockDirect(tracer2, block, tracer);
        }

        private IReadOnlyCollection<ParityLikeTxTrace> TraceBlockDirect(ITracer tracer, Block block, ParityLikeBlockTracer parityTracer, bool isSigned = false)
        {
            using CancellationTokenSource timeout = BuildTimeoutCancellationTokenSource();
            CancellationToken cancellationToken = timeout.Token;
            TraceConstructedBlock(tracer, block, parityTracer.WithCancellation(cancellationToken), isSigned);
            return parityTracer.BuildResult();
        }

        /// <summary>
        /// Traces constructed <paramref name="block"/>: a signed transaction runs with its own nonce, which the processor
        /// validates with its other state checks, while an unsigned call is traced as eth_call runs it.
        /// </summary>
        private static void TraceConstructedBlock(ITracer tracer, Block block, IBlockTracer blockTracer, bool isSigned)
        {
            if (isSigned) tracer.ExecuteSigned(block, blockTracer);
            else tracer.Trace(block, blockTracer);
        }

        /// <summary>A covered block is traced one transaction per worker, rewards last on the seeded end state; any
        /// other block is executed as before. Both produce the traces a full execution produces.</summary>
        private bool TryExecuteBlockInParallel(BlockHeader parent, Block block, ParityTraceTypes types, CancellationToken cancellationToken, [NotNullWhen(true)] out IReadOnlyList<ParityLikeTxTrace>? traces)
        {
            traces = null;
            if (parallelTracer is null) return false;

            return parallelTracer.TryTrace(block, parent,
                PerTransaction(types), Rewards(types), cancellationToken, out traces);
        }

        /// <summary>The same, writing each transaction's traces out as the workers finish them, so the caller reads
        /// the first of them while the block is still being traced and the block is never held whole.</summary>
        private bool TryStreamBlockInParallel(BlockHeader parent, Block block, ParityTraceTypes types, StreamingParityLikeBlockTracer streamingTracer, CancellationToken cancellationToken) =>
            parallelTracer is not null && parallelTracer.TryStream(block, parent,
                PerTransaction(types), Rewards(types), streamingTracer.WriteTraces, cancellationToken);

        private Func<IWorldState, Hash256, IBlockTracer<ParityLikeTxTrace>> PerTransaction(ParityTraceTypes types)
        {
            ParityTraceTypes perTransaction = types & ~ParityTraceTypes.Rewards;
            return (_, txHash) => new ParityLikeBlockTracer(txHash, perTransaction, specProvider);
        }

        private Func<IWorldState, IBlockTracer<ParityLikeTxTrace>>? Rewards(ParityTraceTypes types) =>
            (types & ParityTraceTypes.Rewards) == ParityTraceTypes.Rewards ? _ => new ParityLikeBlockTracer(types, specProvider) : null;

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlockParallelOrReplay(BlockHeader parent, Block block, ParityTraceTypes types, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource? timeout = cancellationToken.CanBeCanceled ? null : BuildTimeoutCancellationTokenSource();
            CancellationToken token = timeout?.Token ?? cancellationToken;
            token.ThrowIfCancellationRequested();
            return TryExecuteBlockInParallel(parent, block, types, token, out IReadOnlyList<ParityLikeTxTrace>? traces)
                ? traces
                : ExecuteBlock(parent, block, new ParityLikeBlockTracer(types, specProvider), cancellationToken: token);
        }

        private IReadOnlyCollection<ParityLikeTxTrace> ExecuteBlock(BlockHeader baseBlock, Block block, ParityLikeBlockTracer tracer, IReleaseSpec? specOverride = null, Hash256? transactionHash = null, CancellationToken cancellationToken = default)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                BlockHeader adjustedHeader = AdjustHeaderForSpec(block.Header, baseBlock, specOverride);
                blockToExecute = block.WithReplacedHeader(adjustedHeader);
            }

            using Scope<ITracer> env = tracerEnv.BuildAndOverrideAtTarget(blockToExecute.Header, specOverride: specOverride);
            ITracer tracer2 = env.Component;

            using CancellationTokenSource? timeout = cancellationToken.CanBeCanceled ? null : BuildTimeoutCancellationTokenSource();
            CancellationToken token = timeout?.Token ?? cancellationToken;
            token.ThrowIfCancellationRequested();
            // A prefix recorded under the block's own spec says nothing about the block run under another one, even
            // though the header keeps its hash: no seed when the spec is overridden.
            tracer2.Execute(blockToExecute, TransactionTraceBoundary.Wrap(tracer.WithCancellation(token), transactionHash, specOverride is null ? prefixSeeds : null));
            return tracer.BuildResult();
        }

        /// <summary>
        /// Adjusts a block header to the minimum needed for execution under a different spec:
        /// fills in <see cref="BlockHeader.BaseFeePerGas"/> when activating EIP-1559 and
        /// <see cref="BlockHeader.ExcessBlobGas"/> when activating EIP-4844.
        /// </summary>
        /// <remarks>
        /// Known limitation: <c>WithdrawalsRoot</c> (EIP-4895), <c>ParentBeaconBlockRoot</c>
        /// (EIP-4788), and <c>RequestsHash</c> (EIP-7685) are not synthesized. When a block is
        /// re-executed under a spec that activates those features for the first time, system
        /// calls (e.g. the beacon-root contract update) will see <c>null</c>/zero inputs and
        /// produce side-effects that don't match a real chain — acceptable for tracing
        /// (NoValidation path) but trace consumers should be aware. The intended use is
        /// pre-merge fork comparison (e.g. Istanbul ↔ Berlin precompile gas), where these
        /// fields are inherently absent.
        /// </remarks>
        private static BlockHeader AdjustHeaderForSpec(BlockHeader header, BlockHeader parentHeader, IReleaseSpec spec)
        {
            BlockHeader adjusted = header.Clone();

            adjusted.BaseFeePerGas = spec.IsEip1559Enabled
                ? adjusted.BaseFeePerGas.IsZero
                    ? BaseFeeCalculator.Calculate(parentHeader, spec)
                    : adjusted.BaseFeePerGas
                : UInt256.Zero;

            adjusted.ExcessBlobGas = spec.IsEip4844Enabled
                ? BlobGasCalculator.CalculateExcessBlobGas(parentHeader, spec)
                : null;

            return adjusted;
        }

        private bool TryResolveForkSpec<TResult>(string? fork, out IReleaseSpec? forkSpec, out ResultWrapper<TResult>? error)
        {
            forkSpec = null;
            error = null;

            if (fork is null)
                return true;

            if (specProvider is not IForkAwareSpecProvider forkAwareProvider)
            {
                error = ResultWrapper<TResult>.Fail("Spec provider does not support fork overrides", ErrorCodes.InvalidParams);
                return false;
            }

            if (string.IsNullOrEmpty(fork))
            {
                error = ResultWrapper<TResult>.Fail("Fork name must not be null or empty", ErrorCodes.InvalidParams);
                return false;
            }

            if (!forkAwareProvider.TryGetForkSpec(fork, out forkSpec))
            {
                error = ResultWrapper<TResult>.Fail($"Unknown fork: '{fork}'. Available: {string.Join(", ", forkAwareProvider.AvailableForks)}", ErrorCodes.InvalidParams);
                return false;
            }

            return true;
        }

        private static ResultWrapper<TResult> GetStateFailureResult<TResult>(BlockHeader header) =>
            ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);

        private CancellationTokenSource BuildTimeoutCancellationTokenSource() =>
            jsonRpcConfig.BuildTimeoutCancellationToken();

        private ResultWrapper<IEnumerable<T>> BuildStreamingMultiResult<T>(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<IEnumerable<T>> runBuffered,
            CancellationTokenSource? timeoutCts = null)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                using (timeoutCts)
                {
                    return ResultWrapper<IEnumerable<T>>.Success(runBuffered());
                }
            }

            timeoutCts ??= BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<IEnumerable<T>>.Success(new ParityTxTraceStreamingResult<T>(runStreaming, timeoutCts, _logger)
                {
                    MaterializeForInProcess = runBuffered,
                });
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }
        }

        private ResultWrapper<ParityTxTraceFromReplay> BuildStreamingSingleResult(
            Action<Utf8JsonWriter, PipeWriter?, CancellationToken> runStreaming,
            Func<ParityTxTraceFromReplay> runBuffered)
        {
            if (!jsonRpcConfig.EnableTracingStreamMode)
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Success(runBuffered());
            }

            CancellationTokenSource timeoutCts = BuildTimeoutCancellationTokenSource();
            try
            {
                return ResultWrapper<ParityTxTraceFromReplay>.Success(new ParityTxTraceFromReplayStreamingResult(runStreaming, timeoutCts, _logger)
                {
                    MaterializeForInProcess = runBuffered,
                });
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }
        }

        private void TraceBlockStreaming(Block block, ParityLikeBlockTracer tracer, CancellationToken ct)
        {
            using Scope<ITracer> env = tracerEnv.BuildAndOverride(block.Header);
            env.Component.Trace(block, tracer.WithCancellation(ct));
        }

        private void ExecuteBlockStreaming(BlockHeader baseHeader, Block block, ParityLikeBlockTracer tracer, CancellationToken ct, IReleaseSpec? specOverride = null, Hash256? transactionHash = null)
        {
            Block blockToExecute = block;
            if (specOverride is not null)
            {
                blockToExecute = block.WithReplacedHeader(AdjustHeaderForSpec(block.Header, baseHeader, specOverride));
            }
            using Scope<ITracer> env = tracerEnv.BuildAndOverrideAtTarget(blockToExecute.Header, specOverride: specOverride);
            env.Component.Execute(blockToExecute, TransactionTraceBoundary.Wrap(tracer.WithCancellation(ct), transactionHash, specOverride is null ? prefixSeeds : null));
        }

        /// <summary>
        /// Trace simulated blocks transactions (eth_simulateV1)
        /// </summary>
        public ResultWrapper<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>> trace_simulateV1(
            SimulatePayload<TransactionForRpc> payload, BlockParameter? blockParameter = null, string[]? traceTypes = null) =>
            TryGetParityTypes(traceTypes ?? ["Trace"], out ParityTraceTypes parityTypes)
                ? new SimulateTxExecutor<ParityLikeTxTrace>(blockchainBridge, blockFinder, jsonRpcConfig, specProvider, new ParityStyleSimulateBlockTracerFactory(types: parityTypes), _secondsPerSlot)
                    .Execute(payload, blockParameter)
                : InvalidTraceTypes<IReadOnlyList<SimulateBlockResult<ParityLikeTxTrace>>>();
    }
}
