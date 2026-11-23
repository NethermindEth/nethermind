// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;
using Nethermind.TxPool;

namespace Nethermind.LightClient.Rpc;

/// <summary>Executes calls against authenticated state, fetching missing state between clean EVM attempts.</summary>
internal sealed partial class EvmExecutor(IExecutionStateSource execution, ISpecProvider specs, ILogManager logs, TimeProvider? clock = null,
    ulong? secondsPerSlot = null, IWithdrawalProcessorFactory? withdrawalProcessorFactory = null) : IDisposable
{
    // Preserve existing log filters when the implementation class is renamed.
    internal const string LogCategory = "LightClient.Rpc.VerifiedCall";
    private const ulong GasCap = 10_000_000;
    private const int CodeCacheCapacity = 256;
    private readonly BoundedMemoryCache _cache = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ICodeCache _codeCache = new StaticCodeCache(CodeCacheCapacity);
    private readonly IContainer _processing = CreateExecutionBuilder(specs, logs)
        .AddScoped<CodeOverrideStore>()
        .AddDecorator<ICodeInfoRepository, OverridableCodeInfoRepository>()
        .AddScoped<IOverridableCodeInfoRepository, ICodeInfoRepository>(static codes => (OverridableCodeInfoRepository)codes)
        .AddScoped<SingleCallRequestState>()
        .BindScoped<IBlobBaseFeeOverrideProvider, SingleCallRequestState>()
        .AddDecorator<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeOverrideCalculatorDecorator>()
        .Build();
    private readonly SemaphoreSlim _slots = new(4);
    private readonly Nethermind.Logging.ILogger _logger = logs.GetLogger(LogCategory);

    internal Task<string> ExecuteAsync(VerifiedHead head, TransactionForRpc request, bool estimateGas, CancellationToken cancellationToken,
        Dictionary<Address, AccountOverride>? stateOverrides = null, BlockOverride? blockOverrides = null) =>
        ExecuteSingleAsync<string>(head, request, estimateGas ? SingleCallKind.EstimateGas : SingleCallKind.Call,
            stateOverrides, blockOverrides, estimateGas ? EstimateGas : RunCall, TimeSpan.FromSeconds(60), cancellationToken);

    private enum SingleCallKind { Call, EstimateGas, AccessList, TraceCall }

    private delegate T SingleCallOperation<T>(ITransactionProcessor processor, IWorldState state, Transaction tx,
        TransactionForRpc request, in BlockExecutionContext context, CancellationToken token);

    private async Task<T> ExecuteSingleAsync<T>(VerifiedHead head, TransactionForRpc request, SingleCallKind kind,
        Dictionary<Address, AccountOverride>? stateOverrides, BlockOverride? blockOverrides, SingleCallOperation<T> run,
        TimeSpan timeLimit, CancellationToken cancellationToken)
    {
        if (BlockchainBridge.GetUnsupportedBlockOverrideError(blockOverrides) is { } error) throw new RpcException(-32602, error);
        if (blockOverrides?.GasLimit > long.MaxValue) throw new RpcException(-32602, "Block gas limit exceeds the supported range.");
        if (stateOverrides?.Count > 128) throw new RpcException(-32602, "At most 128 account overrides are supported.");
        bool trace = _logger.IsDebug;
        using Activity? diagnostic = trace
            ? new Activity("LightClientCall").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        if (trace)
        {
            string operation = kind switch
            {
                SingleCallKind.Call => "call",
                SingleCallKind.EstimateGas => "estimateGas",
                SingleCallKind.AccessList => "createAccessList",
                _ => "traceCall",
            };
            _logger.Debug($"LC call id={diagnostic?.Id} parent={diagnostic?.ParentId} operation={operation} block={head.BlockHash} number={head.Number} stateRoot={head.StateRoot}");
        }
        using CancellationTokenSource timeout = new(timeLimit, _clock);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = deadline.Token;
        long admissionStart = trace ? Stopwatch.GetTimestamp() : 0;
        bool returned = false;
        try
        {
            T result = await ExecuteCoreAsync(head, request, kind, stateOverrides, blockOverrides, run, trace, token);
            returned = true;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(-32002, "Execution request timed out.");
        }
        finally
        {
            if (trace)
                _logger.Debug($"LC call id={diagnostic?.Id} phase=completion outcome={(returned ? "completed" : "failed")} elapsedMs={Stopwatch.GetElapsedTime(admissionStart).TotalMilliseconds:F3}");
        }
    }

    internal async Task ValidateFrameAdmissionAsync(VerifiedHead head, Transaction transaction, BlockHeader contextHeader,
        IReleaseSpec spec, CancellationToken cancellationToken, bool requireReady = false)
    {
        if (FrameTxValidation.ValidationWorkGas(transaction) > TxPoolConfig.DefaultFrameTxMaxVerifyGas
            || FrameTxValidation.ValidationWorkStateGas(transaction) > 500_000)
            throw new RpcException(-32003, "Frame validation prefix exceeds the bounded public-mempool work limit.");
        bool trace = _logger.IsDebug;
        using Activity? diagnostic = trace
            ? new Activity("LightClientCall").SetIdFormat(ActivityIdFormat.W3C).Start() : null;
        if (trace)
            _logger.Debug($"LC call id={diagnostic?.Id} parent={diagnostic?.ParentId} operation=frame-admission block={head.BlockHash} number={head.Number} stateRoot={head.StateRoot}");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60), _clock);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = deadline.Token;
        long admissionStart = trace ? Stopwatch.GetTimestamp() : 0;
        bool returned = false;
        try
        {
            BlockHeader header = await GetHeaderAsync(head, trace, token);
            // A validation prefix can deposit code that is rolled back, so its code cache must not escape admission.
            StaticCodeCache codeCache = new(CodeCacheCapacity);
            for (int attempt = 0; attempt < 128; attempt++)
            {
                token.ThrowIfCancellationRequested();
                MissingCallState missingState;
                await EnterExecutionAsync(trace, token);
                try
                {
                    using ILifetimeScope environment = CreateExecutionScope(head, codeCache);
                    IWorldState state = environment.Resolve<IWorldState>();
                    if (!state.TryBeginScope(header, out IDisposable? scope)) throw new RpcException(-32000, "Verified state is unavailable.");
                    using (scope)
                    {
                        long executionStart = trace ? Stopwatch.GetTimestamp() : 0;
                        bool filling = false;
                        bool completed = false;
                        try
                        {
                            if (transaction.NonceKeys is { } keys)
                            {
                                foreach (UInt256 key in keys)
                                    ExecutionData.ValidateRelayNonce(transaction.Nonce, KeyedNonceManager.CurrentNonceSeq(state, transaction.SenderAddress!, key), requireReady);
                            }
                            else ExecutionData.ValidateRelayNonce(transaction.Nonce, state.GetNonce(transaction.SenderAddress!), requireReady);
                            ITransactionProcessor processor = environment.Resolve<ITransactionProcessor>();
                            BlockExecutionContext context = new(contextHeader, spec);
                            processor.SetBlockExecutionContext(in context);
                            using FrameTxValidationTracer tracer = new(transaction.SenderAddress!, Eip8141Constants.ExpiryVerifierAddress,
                                state, spec, token: token, maxVerifyGas: TxPoolConfig.DefaultFrameTxMaxVerifyGas);
                            TransactionResult result;
                            try
                            {
                                result = processor.Process(transaction, tracer,
                                    ExecutionOptions.Restore | ExecutionOptions.FrameValidationPrefixOnly);
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested && tracer.Violated)
                            {
                                throw new RpcException(-32003, tracer.ViolationReason!);
                            }
                            if (!result || tracer.Violated || tracer.Payer is null)
                                throw new RpcException(-32003, tracer.ViolationReason ?? result.GetErrorMessage(null) ?? "Frame validation prefix failed.");
                            transaction.PayerAddress = tracer.Payer;
                            completed = true;
                            returned = true;
                            return;
                        }
                        catch (MissingCallState missing)
                        {
                            filling = true;
                            if (trace)
                                _logger.Debug($"LC call id={Activity.Current?.Id} phase=execution attempt={attempt + 1} outcome=missing kind={missing.Kind} elapsedMs={Stopwatch.GetElapsedTime(executionStart).TotalMilliseconds:F3}");
                            missingState = missing;
                        }
                        finally
                        {
                            if (trace && !filling)
                                _logger.Debug($"LC call id={Activity.Current?.Id} phase=execution attempt={attempt + 1} outcome={(completed ? "completed" : "failed")} elapsedMs={Stopwatch.GetElapsedTime(executionStart).TotalMilliseconds:F3}");
                        }
                    }
                }
                finally { _slots.Release(); }
                await FillAsync(head, missingState, trace, token);
            }
            throw new RpcException(-32000, "Frame validation accessed too many distinct authenticated state items.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(-32002, "Execution request timed out.");
        }
        finally
        {
            if (trace)
                _logger.Debug($"LC call id={diagnostic?.Id} phase=completion outcome={(returned ? "completed" : "failed")} elapsedMs={Stopwatch.GetElapsedTime(admissionStart).TotalMilliseconds:F3}");
        }
    }

    private async Task EnterExecutionAsync(bool trace, CancellationToken token)
    {
        long started = trace ? Stopwatch.GetTimestamp() : 0;
        bool admitted = false;
        try
        {
            await _slots.WaitAsync(token);
            admitted = true;
        }
        finally
        {
            if (trace)
                _logger.Debug($"LC call id={Activity.Current?.Id} phase=admission admitted={admitted} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
        }
    }

    private ILifetimeScope CreateExecutionScope(VerifiedHead head, ICodeCache? codeCache = null, bool traceCall = false) =>
        _processing.BeginLifetimeScope(new ExecutionState(head, _cache, codeCache ?? _codeCache, TraceCall: traceCall));

    private static ContainerBuilder CreateExecutionBuilder(ISpecProvider specs, ILogManager logs) => new ContainerBuilder()
        .AddModule(new TransactionProcessingModule(initialAccountCapacity: 32))
        .AddSingleton<ISpecProvider>(specs)
        .AddSingleton<ILogManager>(logs)
        .AddScoped<IWorldStateScopeProvider, ILifetimeScope>(static scope =>
        {
            ExecutionState context = (ExecutionState)scope.Tag;
            return new CallState(context.Head, context.Cache);
        })
        .AddScoped<IBlockhashProvider, ILifetimeScope>(static scope =>
        {
            ExecutionState context = (ExecutionState)scope.Tag;
            return new VerifiedBlockhashes(context.Head, context.Cache, context.SimulatedHashes, context.TraceCall);
        })
        .AddScoped<ICodeCache, ILifetimeScope>(static scope => ((ExecutionState)scope.Tag).CodeCache);

    /// <param name="SimulatedHashes">The hashes of the blocks simulated so far on top of <paramref name="Head"/>, by number.</param>
    private sealed record ExecutionState(VerifiedHead Head, BoundedMemoryCache Cache, ICodeCache CodeCache,
        IReadOnlyDictionary<ulong, Hash256>? SimulatedHashes = null, bool TraceCall = false);

    private async Task<T> ExecuteCoreAsync<T>(VerifiedHead head, TransactionForRpc model, SingleCallKind kind,
        Dictionary<Address, AccountOverride>? stateOverrides, BlockOverride? blockOverrides, SingleCallOperation<T> run,
        bool trace, CancellationToken token)
    {
        if (kind == SingleCallKind.EstimateGas && model.Gas == 0) model.Gas = null;
        BlockHeader header = await GetHeaderAsync(head, trace, token);
        BlockHeader callHeader = header.Clone();
        bool noBaseFee = !model.ShouldSetBaseFee();
        if (noBaseFee && kind != SingleCallKind.TraceCall) callHeader.BaseFeePerGas = 0;
        blockOverrides?.ApplyOverrides(callHeader);
        callHeader.IsPostMerge = true;
        IReleaseSpec spec = specs.GetSpec(callHeader);
        if (kind == SingleCallKind.AccessList && !spec.IsEip2930Enabled)
            throw new RpcException(-32000, "Access lists are unavailable before Berlin.");
        if (StateOverrideValidation.GetError(stateOverrides, spec) is { } error) throw new RpcException(-32602, error);
        if (kind == SingleCallKind.TraceCall && model is LegacyTransactionForRpc { ChainId: { } requestedChainId } && requestedChainId != specs.ChainId)
            throw new RpcException(-32000, $"chainId does not match node's (have={requestedChainId}, want={specs.ChainId})");
        Nethermind.Core.Result<Transaction> converted = kind == SingleCallKind.TraceCall
            ? model.ToTraceCallTransaction(spec, GasCap, callHeader.BaseFeePerGas)
            : BlobTransactionForRpc.WithZeroBlobFeeCapOmitted(model).ToValidatedTransaction(gasCap: GasCap, spec: spec);
        if (converted.IsError) throw new RpcException(-32602, converted.Error);
        if (noBaseFee && kind == SingleCallKind.TraceCall) callHeader.BaseFeePerGas = 0;
        Transaction tx = converted.Data;
        tx.SenderAddress ??= Address.Zero;
        tx.Hash = tx.Type <= TxType.FrameTx ? null : tx.CalculateHash();
        bool zeroBlobFee = tx.CarriesBlobs && (tx.MaxFeePerBlobGas ?? UInt256.Zero).IsZero;
        if (zeroBlobFee) tx.MaxFeePerBlobGas = UInt256.Zero;
        UInt256? blobFeeOverride = zeroBlobFee ? UInt256.Zero : blockOverrides?.BlobBaseFee;
        callHeader.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(tx);
        ICodeCache? codeCache = stateOverrides is { Count: > 0 } ? new StaticCodeCache(CodeCacheCapacity) : null;

        for (int attempt = 0; attempt < 128; attempt++)
        {
            token.ThrowIfCancellationRequested();
            MissingCallState missingState;
            await EnterExecutionAsync(trace, token);
            try
            {
                using ILifetimeScope environment = CreateExecutionScope(head, codeCache, traceCall: kind == SingleCallKind.TraceCall);
                if (blobFeeOverride is { } fee) environment.Resolve<SingleCallRequestState>().BlobBaseFeeOverride = fee;
                IWorldState state = environment.Resolve<IWorldState>();
                if (!state.TryBeginScope(header, out IDisposable? scope)) throw new RpcException(-32000, "Verified state is unavailable.");
                using (scope)
                {
                    long executionStart = trace ? Stopwatch.GetTimestamp() : 0;
                    bool filling = false;
                    bool completed = false;
                    try
                    {
                        if (stateOverrides is { Count: > 0 })
                        {
                            IOverridableCodeInfoRepository codes = environment.Resolve<IOverridableCodeInfoRepository>();
                            state.ApplyStateOverrides(codes, stateOverrides, spec, callHeader.Number, merkleize: false);
                        }
                        if (tx.NonceKeys is null) tx.Nonce = state.GetNonce(tx.SenderAddress);
                        ITransactionProcessor processor = environment.Resolve<ITransactionProcessor>();
                        BlockExecutionContext context = blobFeeOverride is { } overriddenFee ? new(callHeader, spec, overriddenFee) : new(callHeader, spec);
                        T result = run(processor, state, tx, model, in context, token);
                        completed = true;
                        return result;
                    }
                    catch (MissingCallState missing)
                    {
                        filling = true;
                        if (trace)
                            _logger.Debug($"LC call id={Activity.Current?.Id} phase=execution attempt={attempt + 1} outcome=missing kind={missing.Kind} elapsedMs={Stopwatch.GetElapsedTime(executionStart).TotalMilliseconds:F3}");
                        missingState = missing;
                    }
                    finally
                    {
                        if (trace && !filling)
                            _logger.Debug($"LC call id={Activity.Current?.Id} phase=execution attempt={attempt + 1} outcome={(completed ? "completed" : "failed")} elapsedMs={Stopwatch.GetElapsedTime(executionStart).TotalMilliseconds:F3}");
                    }
                }
            }
            finally { _slots.Release(); }
            await FillAsync(head, missingState, trace, token);
        }
        throw new RpcException(-32000, "Execution accessed too many distinct state items.");
    }

    private static string EstimateGas(ITransactionProcessor processor, IWorldState state, Transaction tx,
        TransactionForRpc model, in BlockExecutionContext context, CancellationToken token)
    {
        GasEstimator estimator = new(processor, state);
        if (model is FrameTransactionForRpc { Frames: { } frames } frameModel)
        {
            bool[] fillExecution = new bool[frames.Length];
            bool[] fillState = new bool[frames.Length];
            bool missing = false;
            for (int i = 0; i < frames.Length; i++)
            {
                fillExecution[i] = frames[i].ExecutionGas is null;
                fillState[i] = frames[i].StateGas is null;
                missing |= fillExecution[i] || fillState[i];
            }
            if (missing)
            {
                Func<ulong, UInt256>? maxFeeAt = frameModel.PerGasBudget(tx, GasCap) is null
                    ? null : maxGas => frameModel.PerGasBudget(tx, maxGas)!.Value;
                Result<TxFrame[]> filled = estimator.EstimateFrameGas(tx, context, fillExecution, fillState, GasCap,
                    GasEstimator.DefaultErrorMargin, maxFeeAt, token, out bool reverted);
                if (filled.IsError) throw new RpcException(reverted ? 3 : -32000, reverted ? "execution reverted" : filled.Error);
                tx.Frames = filled.Data;
                tx.GasLimit = FrameTxValidation.TotalGasLimit(tx.Frames);
            }
        }
        GasEstimation estimation = estimator.Estimate(tx, in context, gasCap: GasCap, token: token);
        if (estimation.Reverted)
            throw new RpcException(3, TransactionSubstate.BuildRevertMessage(estimation.RevertData ?? [], estimation.Error),
                (estimation.RevertData ?? []).ToHexString(withZeroX: true));
        if (estimation.Error is not null)
            throw new RpcException(-32000, estimation.Error);
        return $"0x{estimation.Gas:x}";
    }

    private static string RunCall(ITransactionProcessor processor, IWorldState state, Transaction tx,
        TransactionForRpc request, in BlockExecutionContext context, CancellationToken token)
    {
        CallOutputTracer tracer = new();
        TransactionResult result = processor.CallAndRestore(tx, in context, tracer.WithCancellation(token));
        string? error = result.GetErrorMessage(tracer.Error);
        if (result.EvmExceptionType == EvmExceptionType.Revert)
            throw new RpcException(3, TransactionSubstate.BuildRevertMessage(tracer.ReturnValue ?? [], error),
                (tracer.ReturnValue ?? []).ToHexString(withZeroX: true));
        if (error is not null)
            throw new RpcException(-32000, error);
        return (tracer.ReturnValue ?? []).ToHexString(withZeroX: true);
    }

    private async Task<BlockHeader> GetHeaderAsync(VerifiedHead head, bool trace, CancellationToken token)
    {
        object key = (head.BlockHash, "header");
        if (_cache.TryGetValue(key, out BlockHeader? cached))
        {
            if (trace) _logger.Debug($"LC call id={Activity.Current?.Id} phase=header source=cache");
            return cached!;
        }
        long headerStart = trace ? Stopwatch.GetTimestamp() : 0;
        bool verified = false;
        try
        {
            BlockHeader header = await execution.GetHeaderAsync(head, token);
            if (header.Hash != head.BlockHash || header.Number != head.Number || header.StateRoot != head.StateRoot)
                throw new RpcException(-32000, "Execution header does not match verified finality.");
            _cache.Set(key, header, 2048);
            verified = true;
            return header;
        }
        finally
        {
            if (trace)
                _logger.Debug($"LC call id={Activity.Current?.Id} phase=header source=execution outcome={(verified ? "verified" : "failed")} elapsedMs={Stopwatch.GetElapsedTime(headerStart).TotalMilliseconds:F3}");
        }
    }

    private async Task FillAsync(VerifiedHead head, MissingCallState missing, bool trace, CancellationToken token)
    {
        long fillStart = trace ? Stopwatch.GetTimestamp() : 0;
        bool filled = false;
        try
        {
            switch (missing.Kind)
            {
                case MissingKind.Account:
                    Account account = await execution.GetAccountAsync(head, missing.Address!, token);
                    _cache.Set((head.StateRoot, missing.Address!), account, 512);
                    break;
                case MissingKind.Storage:
                    if (!_cache.TryGetValue((head.StateRoot, missing.Address!), out Account? owner) || owner is null)
                        throw new RpcException(-32000, "Account proof is unavailable for storage.");
                    UInt256 slot = await execution.GetStorageAsync(head, missing.Address!, owner, missing.Slot, token);
                    _cache.Set((owner.StorageRoot, missing.Address!, missing.Slot), slot, 512);
                    break;
                case MissingKind.Code:
                    Account? codeOwner = null;
                    foreach (Address address in missing.KnownAccounts!)
                    {
                        if (_cache.TryGetValue((head.StateRoot, address), out Account? candidate)
                            && candidate is not null && candidate.CodeHash.ValueHash256 == missing.CodeHash)
                        {
                            codeOwner = candidate;
                            break;
                        }
                    }
                    if (codeOwner is null) throw new RpcException(-32000, "Code hash has no authenticated account.");
                    byte[] code = await execution.GetCodeAsync(codeOwner, token);
                    _cache.Set(missing.CodeHash, code, 256L + code.Length);
                    break;
                case MissingKind.Blockhash:
                    ulong first = head.Number > 256 ? head.Number - 256 : 0;
                    Hash256[] hashes = await execution.GetAncestorHashesAsync(head, first, token);
                    _cache.Set((head.BlockHash, "ancestors"), (first, hashes), 256L + hashes.Length * 64L);
                    break;
            }
            filled = true;
        }
        finally
        {
            if (trace)
                _logger.Debug($"LC call id={Activity.Current?.Id} phase=fill kind={missing.Kind} count=1 outcome={(filled ? "filled" : "failed")} elapsedMs={Stopwatch.GetElapsedTime(fillStart).TotalMilliseconds:F3}");
        }
    }

    public void Dispose()
    {
        _processing.Dispose();
        _simulation.Dispose();
        if (_replay.IsValueCreated) _replay.Value.Dispose();
        _slots.Dispose();
        _cache.Dispose();
    }

    private enum MissingKind { Account, Storage, Code, Blockhash }

    private sealed class MissingCallState(MissingKind kind) : Exception
    {
        public MissingKind Kind { get; } = kind;
        public Address? Address { get; init; }
        public UInt256 Slot { get; init; }
        public ValueHash256 CodeHash { get; init; }
        public IReadOnlyList<Address>? KnownAccounts { get; init; }
    }

    private sealed class CallState(VerifiedHead head, BoundedMemoryCache cache) : IWorldStateScopeProvider
    {
        public bool HasRoot(BlockHeader? baseBlock) => baseBlock?.StateRoot == head.StateRoot;
        public bool HasStateForTargetBlock(BlockHeader targetBlock) => false;
        public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = null;
            return false;
        }
        public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
        {
            scope = HasRoot(baseBlock) ? new Scope(head, cache) : null;
            return scope is not null;
        }

        /// <remarks>
        /// Committed writes stay in the scope, over the authenticated state, so blocks simulated in one scope see the
        /// earlier blocks' changes. The scope never rehashes, so its root stays the authenticated base root and storage
        /// roots are not authoritative.
        /// </remarks>
        private sealed class Scope(VerifiedHead head, BoundedMemoryCache cache) : IWorldStateScopeProvider.IScope, IWorldStateScopeProvider.ICodeDb
        {
            private readonly Dictionary<ValueHash256, byte[]> _newCode = [];
            private readonly HashSet<Address> _accounts = [];
            private readonly Dictionary<Address, Account?> _writtenAccounts = [];
            private readonly Dictionary<Address, WrittenStorage> _writtenStorage = [];
            public Hash256 RootHash => head.StateRoot;
            public bool StorageRootsAreAuthoritative => false;
            public IWorldStateScopeProvider.ICodeDb CodeDb => this;
            public Account? Get(Address address)
            {
                _accounts.Add(address);
                return _writtenAccounts.TryGetValue(address, out Account? written) ? written : GetAuthenticated(address);
            }
            private Account? GetAuthenticated(Address address)
            {
                if (!cache.TryGetValue((head.StateRoot, address), out Account? account))
                    throw new MissingCallState(MissingKind.Account) { Address = address };
                return account == Account.TotallyEmpty ? null : account;
            }
            public void HintGet(Address address, Account? account) { }
            public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
            {
                _accounts.Add(address);
                _writtenStorage.TryGetValue(address, out WrittenStorage? written);
                // Slots the scope has not written still read the authenticated storage, unless the scope cleared it.
                Hash256? root = written?.Cleared == true ? null : GetAuthenticated(address)?.StorageRoot;
                return new Storage(address, root, cache, written);
            }
            public ReadOnlyMemory<byte> GetCode(in ValueHash256 codeHash)
            {
                if (_newCode.TryGetValue(codeHash, out byte[]? staged)) return staged;
                if (cache.TryGetValue(codeHash, out byte[]? code)) return code!;
                throw new MissingCallState(MissingKind.Code) { CodeHash = codeHash, KnownAccounts = [.. _accounts] };
            }
            public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new CodeSetter(_newCode);
            public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);
            public void UpdateRootHash() { }
            public void Commit(ulong blockNumber) { }
            public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;
            public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken) => throw new NotSupportedException();
            public void Dispose() { }

            private WrittenStorage GetOrCreateStorage(Address address)
            {
                if (!_writtenStorage.TryGetValue(address, out WrittenStorage? written))
                    _writtenStorage[address] = written = new();
                return written;
            }

            private sealed class WriteBatch(Scope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
            {
                public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;
                public void Set(Address key, Account? account)
                {
                    scope._writtenAccounts[key] = account;
                    if (account is null) scope.GetOrCreateStorage(key).Clear();
                    OnAccountUpdated?.Invoke(this, new(key, account));
                }
                // Root commits create batches serially, then hand each worker a distinct account's storage.
                public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
                    new StorageWriteBatch(scope.GetOrCreateStorage(key));
                public void Dispose() { }
            }

            private sealed class StorageWriteBatch(WrittenStorage written) : IWorldStateScopeProvider.IStorageWriteBatch
            {
                public void Set(in UInt256 index, in UInt256 value) => written.Slots[index] = value;
                public void Clear() => written.Clear();
                public void Dispose() { }
            }
        }

        private sealed class WrittenStorage
        {
            public bool Cleared { get; private set; }
            public Dictionary<UInt256, UInt256> Slots { get; } = [];

            public void Clear()
            {
                Cleared = true;
                Slots.Clear();
            }
        }

        private sealed class Storage(Address address, Hash256? authenticatedRoot, BoundedMemoryCache cache, WrittenStorage? written) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => written?.Cleared == true ? Keccak.EmptyTreeHash : authenticatedRoot ?? Keccak.EmptyTreeHash;
            public void Get(in UInt256 index, out UInt256 value)
            {
                if (written is not null && written.Slots.TryGetValue(index, out value)) return;
                Hash256 root = RootHash;
                if (root == Keccak.EmptyTreeHash) { value = UInt256.Zero; return; }
                if (!cache.TryGetValue((root, address, index), out value))
                    throw new MissingCallState(MissingKind.Storage) { Address = address, Slot = index };
            }
            public void HintSet(in UInt256 index) { }
        }

        private sealed class CodeSetter(Dictionary<ValueHash256, byte[]> code) : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> bytes) => code[codeHash] = bytes.ToArray();
            public void Dispose() { }
        }
    }

    private sealed class VerifiedBlockhashes(VerifiedHead head, BoundedMemoryCache cache,
        IReadOnlyDictionary<ulong, Hash256>? simulated, bool traceCall) : IBlockhashProvider
    {
        public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
        {
            if (number >= currentBlock.Number) return null;
            ulong depth = currentBlock.Number - number;
            if (depth > 256) return null;
            if (simulated is null && currentBlock.Hash == head.BlockHash && currentBlock.Number != head.Number)
            {
                if (number >= head.Number)
                    return traceCall && number == head.Number && depth == 1 ? head.BlockHash : null;
                if (head.Number - number == 1) return currentBlock.ParentHash;
                if (head.Number - number > 256)
                    throw new RpcException(-32001, "An authenticated block hash outside the retained execution window was requested.");
            }
            else if (depth == 1) return currentBlock.ParentHash;
            if (simulated is not null)
            {
                if (number > head.Number) return simulated[number];
                if (number == head.Number) return head.BlockHash;
            }
            if (cache.TryGetValue((head.BlockHash, "ancestors"), out (ulong First, Hash256[] Hashes) ancestors)
                && number >= ancestors.First && number - ancestors.First < (ulong)ancestors.Hashes.Length)
                return ancestors.Hashes[number - ancestors.First];
            throw new MissingCallState(MissingKind.Blockhash);
        }
        public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
    }
}
