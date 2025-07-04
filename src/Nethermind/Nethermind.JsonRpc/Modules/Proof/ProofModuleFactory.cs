// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class ProofModuleFactory(
        ILifetimeScope rootLifetimeScope,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory
    ) : ModuleFactoryBase<IProofRpcModule>
    {

        public override IProofRpcModule Create()
        {
            // Note: No overridable world scope here. So there aren't any risk of leaking KV store.
            IReadOnlyTxProcessingScope txProcessingEnv = readOnlyTxProcessingEnvFactory.Create().Build(Keccak.EmptyTreeHash);

            ILifetimeScope tracerScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            {
                builder

                    // Standard read only chain setting
                    .Bind<IBlockProcessor.IBlockTransactionsExecutor, IValidationTransactionExecutor>()
                    .AddScoped<ITransactionProcessorAdapter, TraceTransactionProcessorAdapter>()
                    .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                    .AddInstance<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                    .AddInstance<IBlockValidator>(Always.Valid) // Why?

                    // Specific for proof rpc
                    .AddInstance<IReceiptStorage>(new InMemoryReceiptStorage()) // Umm.... not `NullReceiptStorage`?
                    .AddInstance<IRewardCalculator>(NoBlockRewards.Instance)
                    .AddInstance<IVisitingWorldState>(txProcessingEnv.WorldState).AddInstance<IWorldState>(txProcessingEnv.WorldState)

                    .AddScoped<ITracer, Tracer>()
                    ;
            });

            // The tracer need a in memory receipts while the proof RPC does not.
            // Eh, its a good idea to separate what need block processing and what does not anyway.
            ILifetimeScope proofRpcScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            {
                builder.AddInstance<ITracer>(tracerScope.Resolve<ITracer>());
            });

            proofRpcScope.Disposer.AddInstanceForAsyncDisposal(tracerScope);
            rootLifetimeScope.Disposer.AddInstanceForDisposal(proofRpcScope);

            return proofRpcScope.Resolve<IProofRpcModule>();
        }
    }
}
