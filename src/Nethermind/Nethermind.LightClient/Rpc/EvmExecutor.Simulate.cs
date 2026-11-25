// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.ExceptionServices;
using System.Text.Json;
using Autofac;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Facade.Simulate;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.State.OverridableEnv;
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
                    SimulationBridge bridge = new(environment, simulatedHashes, specs);
                    // The base header is always passed in, so the executor never looks a block up.
                    SimulateTxExecutor<SimulateCallResult> executor = new(bridge, null, _simulateConfig, specs,
                        new SimulateBlockMutatorTracerFactory(), environment.Resolve<IBlocksConfig>().SecondsPerSlot);
                    ResultWrapper<IReadOnlyList<SimulateBlockResult<SimulateCallResult>>> result = executor.Execute(
                        ReadPayload(payload), null, searchResult: new SearchResult<BlockHeader>(header), cancellationToken: token);
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

    private sealed class SimulationBridge(ILifetimeScope environment, Dictionary<ulong, Hash256> simulatedHashes, ISpecProvider specs)
        : ISimulateBridge
    {
        public ulong GetChainId() => specs.ChainId;

        public bool HasStateForBlock(BlockHeader? baseBlock) => environment.Resolve<IWorldStateScopeProvider>().HasRoot(baseBlock);

        public SimulateOutput<TTrace> Simulate<TTrace>(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> payload,
            ISimulateBlockTracerFactory<TTrace> tracerFactory, ulong gasCapLimit, CancellationToken token)
        {
            IWorldState state = environment.Resolve<IWorldState>();
            if (!state.TryBeginScope(header, out IDisposable? scope)) throw new RpcException(-32000, "Verified state is unavailable.");
            using (scope)
            {
                SimulateRequestState request = environment.Resolve<SimulateRequestState>();
                request.Validate = payload.Validation;
                IBlockTracer<TTrace> tracer = tracerFactory.CreateSimulateBlockTracer(payload.TraceTransfers, state, specs, header, token);
                try
                {
                    SimulateBridgeHelper helper = new(environment.Resolve<IBlocksConfig>(), specs);
                    SimulateBridgeHelper.ExecutionEnvironment execution = new(state, specs,
                        environment.Resolve<IOverridableCodeInfoRepository>(), request, environment.Resolve<IBlockProcessor>());
                    return helper.TrySimulate(header, payload, tracer, execution, gasCapLimit, token,
                        blockFinalized: block => simulatedHashes[block.Number] = block.Hash!,
                        propagateException: static exception => exception is MissingCallState or OperationCanceledException or RpcException
                            or ContractException { InnerException: MissingCallState or OperationCanceledException });
                }
                catch (ContractException exception) when (exception.InnerException is MissingCallState or OperationCanceledException)
                {
                    // A withdrawal system transaction needs the same proof retry and cancellation as a user call.
                    ExceptionDispatchInfo.Throw(exception.InnerException);
                    throw;
                }
                finally
                {
                    tracer.TryDispose();
                }
            }
        }
    }
}
