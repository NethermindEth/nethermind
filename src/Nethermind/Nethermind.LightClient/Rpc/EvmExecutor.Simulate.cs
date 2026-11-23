// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State;
using Nethermind.State.OverridableEnv;
using Nethermind.Trie;
using ResultType = Nethermind.Core.ResultType;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EvmExecutor
{
    // Every state item a simulation touches first costs a proof round trip, so the limits stay far below a full node's.
    private readonly JsonRpcConfig _simulateConfig = new()
    {
        GasCap = GasCap,
        MaxSimulateBlocksCap = 16,
        MaxSimulateCallsCap = 64,
        MaxSimulateCallsPerBlockCap = 64,
    };
    private static ContainerBuilder CreateBlockExecutionBuilder(ISpecProvider specs, ILogManager logs, ulong? secondsPerSlot,
        IWithdrawalProcessorFactory? withdrawalProcessorFactory) =>
        CreateExecutionBuilder(specs, logs)
        .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()
        .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>()
        .AddScoped<IBeaconBlockRootHandler, BeaconBlockRootHandler>()
        .AddScoped<IBlockhashStore, BlockhashStore>()
        .AddSingleton(withdrawalProcessorFactory ?? new WithdrawalProcessorFactory(logs))
        .AddScoped<IWithdrawalProcessor, IWithdrawalProcessorFactory, IWorldState, ITransactionProcessor>(
            static (factory, state, processor) => factory.Create(state, processor))
        .AddScoped<IExecutionRequestsProcessor, ExecutionRequestsProcessor>()
        .AddScoped<BalTxProcessorFactory>()
        .AddScoped<IBlockAccessListManager, BlockAccessListManager>()
        // A BAL processor needs code for its own world state, not the main processor's repository.
        .AddScoped<CodeInfoRepositoryFactory, IPrecompileProvider, ICodeCache>(static (precompiles, codeCache) =>
            worldState => new CacheCodeInfoRepository(worldState, precompiles, codeCache))
        .AddSingleton<IBlocksConfig>(secondsPerSlot is { } slotSeconds ? new BlocksConfig { SecondsPerSlot = slotSeconds } : new BlocksConfig())
        .AddSingleton<IRewardCalculator>(NoBlockRewards.Instance)
        .AddSingleton<IBlockValidator>(Always.Valid)
        .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance);

    private readonly IContainer _simulation = CreateBlockExecutionBuilder(specs, logs, secondsPerSlot, withdrawalProcessorFactory)
        .AddScoped<IBlockProcessor, StandardBlockProcessor>()
        // The simulation pieces of SimulateReadOnlyBlocksProcessingEnvFactory and OverridableEnvFactory.
        .AddScoped<CodeOverrideStore>()
        .AddDecorator<ICodeInfoRepository, OverridableCodeInfoRepository>()
        .AddScoped<IOverridableCodeInfoRepository, ICodeInfoRepository>(static codes => (OverridableCodeInfoRepository)codes)
        .AddScoped<SimulateRequestState>()
        .BindScoped<IBlobBaseFeeOverrideProvider, SimulateRequestState>()
        .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeOverrideCalculatorDecorator>()
        .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, SimulateBlockValidationTransactionsExecutor>()
        .Intercept<ITransactionProcessor>(SkipSenderChecksTransactionProcessorFactory.Apply)
        .AddDecorator<ITransactionProcessorFactory>(static (_, inner) => new SkipSenderChecksTransactionProcessorFactory(inner))
        .AddDecorator<CodeInfoRepositoryFactory>(static (context, inner) =>
        {
            CodeOverrideStore overrides = context.Resolve<CodeOverrideStore>();
            return worldState => new MovedPrecompileCodeInfoRepository(inner(worldState), worldState, overrides);
        })
        .AddScoped<TransactionProcessorAdapterFactory, SimulateRequestState, IWorldState>(static (request, state) =>
            processor => new SimulateTransactionProcessorAdapter(processor, request, state))
        .AddScoped<ITransactionProcessorAdapter, ITransactionProcessor, TransactionProcessorAdapterFactory>(
            static (processor, adapterFactory) => adapterFactory(processor))
        .Build();

    /// <summary>Runs <c>eth_simulateV1</c> on top of <paramref name="head"/>, fetching missing state between clean attempts.</summary>
    /// <param name="payload">The JSON simulation payload, read afresh for every attempt because the simulator mutates it.</param>
    /// <remarks>
    /// Blocks are processed by Nethermind's block processor with the full node's simulation decorators, over the
    /// authenticated state plus the changes of the earlier simulated blocks. The light client holds no trie, so a
    /// simulated block's <c>stateRoot</c>, and the block hash derived from it, use the base block's state root.
    /// </remarks>
    internal async Task<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> SimulateAsync(VerifiedHead head, JsonElement payload,
        CancellationToken cancellationToken)
    {
        bool trace = _logger.IsDebug;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60), _clock);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = deadline.Token;
        try
        {
            BlockHeader header = await GetHeaderAsync(head, trace, token);
            // Simulated deployments and code overrides must not reach the shared code cache.
            StaticCodeCache codeCache = new(CodeCacheCapacity);
            for (int attempt = 0; attempt < 128; attempt++)
            {
                token.ThrowIfCancellationRequested();
                MissingCallState missingState;
                await EnterExecutionAsync(trace, token);
                try
                {
                    Dictionary<ulong, Hash256> simulatedHashes = [];
                    using ILifetimeScope environment = _simulation.BeginLifetimeScope(new ExecutionState(head, _cache, codeCache, simulatedHashes));
                    SimulationBridge bridge = new(this, environment, simulatedHashes, specs.ChainId);
                    // The base header is always passed in, so the executor never looks a block up.
                    SimulateTxExecutor<SimulateCallResult> executor = new(bridge, null!, _simulateConfig, specs,
                        new SimulateBlockMutatorTracerFactory(), environment.Resolve<IBlocksConfig>().SecondsPerSlot);
                    ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(
                        ReadPayload(payload), null, searchResult: new SearchResult<BlockHeader>(header.Clone()), cancellationToken: token);
                    if (result.Result.ResultType == ResultType.Failure)
                        throw new RpcException(result.ErrorCode, result.Result.Error ?? "Simulation failed.");
                    return result.Data;
                }
                catch (MissingCallState missing)
                {
                    missingState = missing;
                }
                finally { _slots.Release(); }
                await FillAsync(head, missingState, trace, token);
            }
            throw new RpcException(-32000, "Simulation accessed too many distinct state items.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(-32002, "Execution request timed out.");
        }
    }

    private static SimulatePayload<TransactionForRpc> ReadPayload(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<SimulatePayload<TransactionForRpc>>(EthereumJsonSerializer.JsonOptions)
                ?? throw new RpcException(-32602, "Invalid verified RPC parameters.");
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new RpcException(-32602, "Invalid verified RPC parameters.");
        }
    }

    /// <remarks>
    /// Mirrors <see cref="SimulateBridgeHelper.TrySimulate{TTrace}"/> without a block tree: the light client keeps the
    /// simulated blocks' hashes for <c>BLOCKHASH</c> itself. Missing state and cancellation are not simulation errors, so
    /// they leave the attempt rather than become part of the output.
    /// </remarks>
    private SimulateOutput<TTrace> Simulate<TTrace>(ILifetimeScope environment, Dictionary<ulong, Hash256> simulatedHashes, BlockHeader parent,
        SimulatePayload<TransactionWithSourceDetails> payload, ISimulateBlockTracerFactory<TTrace> tracerFactory, ulong gasCap,
        CancellationToken token)
    {
        IWorldState state = environment.Resolve<IWorldState>();
        SimulateRequestState request = environment.Resolve<SimulateRequestState>();
        List<SimulateBlockResult<TTrace>> blocks = [];
        SimulateOutput<TTrace> output = new() { Items = blocks };
        if (!state.TryBeginScope(parent, out IDisposable? scope)) throw new RpcException(-32000, "Verified state is unavailable.");
        using (scope)
        {
            request.Validate = payload.Validation;
            request.TotalGasLeft = gasCap;
            IBlockTracer<TTrace> tracer = tracerFactory.CreateSimulateBlockTracer(payload.TraceTransfers, state, specs, parent, token);
            try
            {
                SimulateBlocks(environment, state, request, simulatedHashes, parent, payload, tracer, blocks, token);
            }
            catch (ArgumentException exception)
            {
                output.Error = exception.Message;
                output.IsInvalidInput = true;
            }
            catch (InvalidTransactionException exception)
            {
                output.Error = exception.Reason.ErrorDescription;
                output.TransactionResult = exception.Reason;
            }
            catch (InsufficientBalanceException exception)
            {
                output.Error = exception.Message;
                output.TransactionResult = TransactionResult.InsufficientSenderBalance;
            }
            catch (ContractException exception) when (exception.InnerException is MissingCallState or OperationCanceledException)
            {
                // A withdrawal system transaction needs the same proof retry and cancellation as a user call.
                ExceptionDispatchInfo.Throw(exception.InnerException);
            }
            catch (Exception exception) when (exception is not (MissingCallState or OperationCanceledException or RpcException))
            {
                output.Error = exception.Message;
            }
            finally
            {
                tracer.TryDispose();
            }
        }
        return output;
    }

    private void SimulateBlocks<TTrace>(ILifetimeScope environment, IWorldState state, SimulateRequestState request,
        Dictionary<ulong, Hash256> simulatedHashes, BlockHeader parent, SimulatePayload<TransactionWithSourceDetails> payload,
        IBlockTracer<TTrace> tracer, List<SimulateBlockResult<TTrace>> blocks, CancellationToken token)
    {
        SimulateBridgeHelper helper = new(environment.Resolve<IBlocksConfig>(), specs);
        IBlockProcessor processor = environment.Resolve<IBlockProcessor>();
        IOverridableCodeInfoRepository codes = environment.Resolve<IOverridableCodeInfoRepository>();
        IBlockTracer cancellableTracer = tracer.WithCancellation(token);
        ProcessingOptions options = payload.Validation
            ? SimulateBridgeHelper.SimulateProcessingOptions
            : SimulateBridgeHelper.SimulateProcessingOptions | ProcessingOptions.NoValidation;
        foreach (BlockStateCall<TransactionWithSourceDetails> blockCall in payload.BlockStateCalls ?? [])
        {
            (BlockHeader callHeader, IReleaseSpec spec) = helper.GetCallHeader(specs, blockCall, parent, payload.Validation);
            request.BlockGasLeft = callHeader.GasLimit;
            request.BlockStateGasLeft = callHeader.GasLimit;
            callHeader.Hash = callHeader.CalculateHash();
            TransactionWithSourceDetails[] calls = blockCall.Calls ?? [];
            request.Calls = calls;
            SimulateBridgeHelper.PrepareState(blockCall, state, codes, callHeader.Number, spec);
            Block block = new(callHeader, helper.AssembleBody(calls, blockCall.BlockOverrides?.Withdrawals, spec));
            request.BlobBaseFeeOverride = spec.IsEip4844Enabled ? blockCall.BlockOverrides?.BlobBaseFee : null;

            (Block processed, _) = processor.ProcessOne(block, options, cancellableTracer, spec, token);
            simulatedHashes[processed.Number] = SimulateBridgeHelper.FinalizeBlock(processed, spec, state, tracer);

            blocks.Add(new SimulateBlockResult<TTrace>(processed, payload.ReturnFullTransactionObjects, specs)
            {
                Calls = [.. tracer.BuildResult()],
            });
            parent = processed.Header;
        }
    }

    /// <summary>The part of <see cref="IBlockchainBridge"/> that <see cref="SimulateTxExecutor{TTrace}"/> uses.</summary>
    private sealed class SimulationBridge(EvmExecutor owner, ILifetimeScope environment, Dictionary<ulong, Hash256> simulatedHashes, ulong chainId)
        : IBlockchainBridge
    {
        public ulong GetChainId() => chainId;
        public bool HasStateForBlock(BlockHeader? baseBlock) => true;
        public SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload,
            ISimulateBlockTracerFactory<TTrace> simulateBlockTracerFactory, ulong gasCapLimit, CancellationToken cancellationToken) =>
            owner.Simulate(environment, simulatedHashes, header, payload, simulateBlockTracerFactory, gasCapLimit, cancellationToken);

        public Block HeadBlock => throw new NotSupportedException();
        public bool IsMining => throw new NotSupportedException();
        public void RecoverTxSenders(Block block) => throw new NotSupportedException();
        public Address? RecoverTxSender(Transaction tx) => throw new NotSupportedException();
        public Address? RecoverTxSender(Transaction tx, bool useSignatureChainId) => throw new NotSupportedException();
        public TxReceipt GetReceipt(Hash256 txHash) => throw new NotSupportedException();
        public (TxReceipt? Receipt, ulong BlockTimestamp, TxGasInfo? GasInfo, int LogIndexStart) GetTxReceiptInfo(Hash256 txHash) =>
            throw new NotSupportedException();
        public bool TryGetTransaction(Hash256 txHash, [NotNullWhen(true)] out TransactionLookupResult? result, bool checkTxnPool = true) =>
            throw new NotSupportedException();
        public CallOutput Call(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride = null,
            UInt256? blobBaseFeeOverride = null, BlockOverride? blockOverride = null, bool noBaseFee = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public CallOutput EstimateGas(BlockHeader header, Transaction tx, int errorMarginBasisPoints,
            Dictionary<Address, AccountOverride>? stateOverride = null, UInt256? blobBaseFeeOverride = null, BlockOverride? blockOverride = null,
            ulong gasCap = 0, bool noBaseFee = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Result<TxFrame[]> EstimateFrameGas(BlockHeader header, Transaction tx, bool[] fillExecution, bool[] fillState, ulong gasCap,
            int errorMargin, Func<ulong, UInt256>? maxFeeAt, Dictionary<Address, AccountOverride>? stateOverride, BlockOverride? blockOverride,
            CancellationToken cancellationToken, out bool executionReverted) => throw new NotSupportedException();
        public CallOutput CreateAccessList(BlockHeader header, Transaction tx, Dictionary<Address, AccountOverride>? stateOverride, bool optimize,
            UInt256? blobBaseFeeOverride = null, bool noBaseFee = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public int NewBlockFilter() => throw new NotSupportedException();
        public int NewPendingTransactionFilter() => throw new NotSupportedException();
        public int NewFilter(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? address = null,
            IEnumerable<Hash256[]?>? topics = null) => throw new NotSupportedException();
        public bool UninstallFilter(int filterId) => throw new NotSupportedException();
        public bool FilterExists(int filterId) => throw new NotSupportedException();
        public ArrayPoolList<Hash256> GetBlockFilterChanges(int filterId) => throw new NotSupportedException();
        public ArrayPoolList<Hash256> GetPendingTransactionFilterChanges(int filterId) => throw new NotSupportedException();
        public ArrayPoolList<FilterLog> GetLogFilterChanges(int filterId) => throw new NotSupportedException();
        public FilterType GetFilterType(int filterId) => throw new NotSupportedException();
        public LogFilter GetFilter(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? addresses = null,
            IEnumerable<Hash256[]?>? topics = null) => throw new NotSupportedException();
        public IEnumerable<FilterLog> GetLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IEnumerable<FilterLog> GetLogs(BlockParameter fromBlock, BlockParameter toBlock, HashSet<AddressAsKey>? addresses = null,
            IEnumerable<Hash256[]?>? topics = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public LogFilter? GetLogFilter(int filterId) => throw new NotSupportedException();
        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingStats? diagnostics = null)
            where TCtx : struct, INodeContext<TCtx> => throw new NotSupportedException();
        public Witness GenerateExecutionWitness(BlockHeader parent, Block block) => throw new NotSupportedException();
        public Witness GenerateExecutionWitness(BlockHeader parent, Block block, IBlockTracer tracer) => throw new NotSupportedException();
        public SingleCallWitnessResult GenerateExecutionWitness(BlockHeader header, Transaction tx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ReadOnlyBlockAccessList? GetBlockAccessList(ulong blockNumber, Hash256 blockHash) => throw new NotSupportedException();
        public MemoryManager<byte>? GetBlockAccessListRlp(ulong blockNumber, Hash256 blockHash) => throw new NotSupportedException();
        public void DeleteBlockAccessList(ulong blockNumber, Hash256 blockHash) => throw new NotSupportedException();
        public IEnumerable<FilterLog> FindLogs(LogFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IEnumerable<FilterLog> FindLogs(LogFilter filter, BlockHeader fromBlock, BlockHeader toBlock,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
