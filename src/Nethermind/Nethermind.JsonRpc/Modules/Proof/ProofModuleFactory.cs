// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State.OverridableEnv;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory(
        ILifetimeScope rootLifetimeScope,
        IOverridableEnvFactory overridableEnvFactory
    ) : ModuleFactoryBase<IProofRpcModule>
    {

        public override IProofRpcModule Create()
        {
            IOverridableEnv overridableEnv = overridableEnvFactory.Create();

            ILifetimeScope tracerScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddModule(overridableEnv)

                // Standard read only chain setting
                .Bind<IBlockProcessor.IBlockTransactionsExecutor, IValidationTransactionExecutor>()
                .AddScoped<ITransactionProcessorAdapter, TraceTransactionProcessorAdapter>()
                .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                .AddScoped<IBlockValidator>(Always.Valid) // Why?

                // Specific for proof rpc
                .AddScoped<IReceiptStorage>(new InMemoryReceiptStorage()) // Umm.... not `NullReceiptStorage`?
                .AddScoped<IRewardCalculator>(NoBlockRewards.Instance)

                .AddScoped<ITracer, Tracer>());

            // The tracer need a in memory receipts while the proof RPC does not.
            // Eh, its a good idea to separate what need block processing and what does not anyway.
            ILifetimeScope proofRpcScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddSingleton<IOverridableEnv<ITracer>>(tracerScope.Resolve<IOverridableEnv<ITracer>>()));

            proofRpcScope.Disposer.AddInstanceForAsyncDisposal(tracerScope);
            rootLifetimeScope.Disposer.AddInstanceForDisposal(proofRpcScope);

            return proofRpcScope.Resolve<IProofRpcModule>();
        }
    }
}
