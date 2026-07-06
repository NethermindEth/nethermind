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
        IProcessingEnvBuilder envBuilder,
        IOverridableEnvFactory overridableEnvFactory
    ) : ModuleFactoryBase<IProofRpcModule>
    {
        public override IProofRpcModule Create()
        {
            IOverridableEnvHandle<ITracer> tracer = envBuilder
                .WithOverridableEnv(overridableEnvFactory.Create())
                // Standard read only chain setting
                .WithBlockValidationConfiguration()
                .WithReplacedComponent<ITransactionProcessorAdapter, TraceTransactionProcessorAdapter>()
                .WithReplacedComponent<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                .WithReplacedComponent<IBlockValidator>(Always.Valid)
                // Specific for proof rpc
                .WithReplacedComponent<IReceiptStorage>(new InMemoryReceiptStorage())
                .WithReplacedComponent<IRewardCalculator>(NoBlockRewards.Instance)
                .WithReplacedComponent<ITracer, Tracer>()
                .Configure(builder => builder.AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>())
                .BuildAsOverridableEnv<ITracer>();

            // The tracer needs an in-memory receipts store while the proof RPC does not; keep them separate.
            // IWitnessGeneratingBlockProcessingEnvFactory used by proof_call is resolved from the parent scope.
            ILifetimeScope proofRpcScope = rootLifetimeScope.BeginLifetimeScope((builder) => builder
                .AddSingleton<IOverridableEnv<ITracer>>(tracer));

            proofRpcScope.Disposer.AddInstanceForAsyncDisposal(tracer);
            rootLifetimeScope.Disposer.AddInstanceForDisposal(proofRpcScope);

            return proofRpcScope.Resolve<IProofRpcModule>();
        }
    }
}
