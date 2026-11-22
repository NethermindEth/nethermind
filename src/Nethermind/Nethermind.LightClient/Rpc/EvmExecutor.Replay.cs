// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Call;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class EvmExecutor
{
    private readonly Lazy<IContainer> _replay = new(() => CreateBlockExecutionBuilder(specs, logs, secondsPerSlot)
        .AddSingleton<IEthereumEcdsa>(new EthereumEcdsa(specs.ChainId))
        // Bind the adapter to the supplied processor, including when a BAL processor is created.
        .AddSingleton<TransactionProcessorAdapterFactory>(static (ITransactionProcessor processor) => new ExecuteTransactionProcessorAdapter(processor))
        .AddScoped<ITransactionProcessorAdapter, ITransactionProcessor, TransactionProcessorAdapterFactory>(
            static (processor, adapterFactory) => adapterFactory(processor))
        .AddScoped<RecoverSignatures>()
        .AddScoped<TransactionTraceExecutor>()
        .AddScoped<IBlockProcessor, TransactionTraceBlockProcessor>()
        .Build());

    internal async Task<GethLikeTxTrace> TraceTransactionAsync(GethTraceOptions options, TimeSpan timeLimit,
        Func<CancellationToken, Task<(Block Block, int Index)?>> findTransaction, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(timeLimit, _clock);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = deadline.Token;
        try
        {
            (Block block, int index) = await findTransaction(token)
                ?? throw new RpcException(-32001, "Transaction is unavailable in the authenticated history window.");
            if (block.IsGenesis || index >= 64)
                throw new RpcException(-32005, "Transaction tracing is limited to the first 64 transactions of a non-genesis block.");
            BlockHeader parent = await execution.GetHeaderByHashAsync(block.ParentHash!, token);
            LogHeaderChain.Verify(parent, block.Number - 1, block.ParentHash!);
            VerifiedHead head = new(0, parent.Number, parent.Hash!, parent.StateRoot!);

            TxReceipt[] receipts = await execution.GetReceiptsAsync(block.Header, token, block.Transactions.Length);
            ExecutionData.ValidateReceipts(block, receipts);
            if (receipts[index].GasUsedTotal > GasCap)
                throw new RpcException(-32005, "Transaction tracing is limited to 10000000 cumulative gas.");
            int logIndex = 0;
            for (int i = 0; i < index; i++) logIndex = checked(logIndex + (receipts[i].Logs?.Length ?? 0));

            bool trace = _logger.IsDebug;
            bool withLog = NativeCallTracerConfig.FromJson(options.TracerConfig, Nethermind.Serialization.Json.EthereumJsonSerializer.JsonOptions).WithLog;
            StaticCodeCache codeCache = new(CodeCacheCapacity);
            BlockHeader header = block.Header.Clone();
            header.IsPostMerge = true;
            IReleaseSpec spec = specs.GetSpec(header);
            Hash256 target = block.Transactions[index].Hash!;
            for (int attempt = 0; attempt < 128; attempt++)
            {
                token.ThrowIfCancellationRequested();
                MissingCallState missingState;
                await EnterExecutionAsync(trace, token);
                try
                {
                    using ILifetimeScope environment = _replay.Value.BeginLifetimeScope(new ExecutionState(head, _cache, codeCache));
                    IWorldState state = environment.Resolve<IWorldState>();
                    if (!state.TryBeginScope(parent, out IDisposable? scope)) throw new RpcException(-32001, "Authenticated parent state is unavailable.");
                    using (scope)
                    {
                        // The trace processor writes recovered senders and spent gas into these transactions.
                        Block prefix = new Block(header, block.Body).WithOwnTransactions(index + 1);
                        using PrefixCallTracer tracer = new(target, spec, options with { LogIndex = withLog ? new BlockLogIndex(logIndex) : null });
                        IBlockTracer boundary = TransactionTraceBoundary.Wrap(tracer.WithCancellation(token), target);
                        environment.Resolve<IBlockProcessor>().ProcessOne(prefix, TraceProcessingOptions.ReadOnlySigned, boundary, spec, token);
                        return tracer.TakeResult() ?? throw new RpcException(-32000, "The authenticated transaction did not produce a trace.");
                    }
                }
                catch (MissingCallState missing) { missingState = missing; }
                finally { _slots.Release(); }
                await FillAsync(head, missingState, trace, token);
            }
            throw new RpcException(-32000, "Transaction replay accessed too many distinct state items.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(-32002, "Transaction replay timed out.");
        }
    }

    private sealed class PrefixCallTracer(Hash256 target, IReleaseSpec spec, GethTraceOptions options) : BlockTracer, IDisposable
    {
        private NativeCallTracer? _tracer;
        private GethLikeTxTrace? _result;

        public override ITxTracer StartNewTxTrace(Transaction? tx)
        {
            if (tx?.Hash != target) return NullTxTracer.Instance;
            _tracer = new(tx, spec, options);
            return new CompositeTxTracer(new CallTraceBudget(_tracer.IsTracingLogs), _tracer);
        }

        public override void EndTxTrace()
        {
            // StartNewTxTrace hands the tracer to the shared executor, which disposes it after this callback.
            if (_tracer is not null) _result = _tracer.BuildResult();
            _tracer = null;
        }

        public GethLikeTxTrace? TakeResult()
        {
            GethLikeTxTrace? result = _result;
            _result = null;
            return result;
        }

        public void Dispose() => _result?.Dispose();
    }
}
