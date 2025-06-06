// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Proof
{
    public class AutoProofModuleFactory(
        ILifetimeScope rootLifetimeScope,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory
    ) : ModuleFactoryBase<IProofRpcModule>
    {

        public override IProofRpcModule Create()
        {
            IReadOnlyTxProcessingScope txProcessingEnv = readOnlyTxProcessingEnvFactory.Create().Build(Keccak.EmptyTreeHash);
            ILifetimeScope blockProcessingScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            {
                builder
                    .AddScoped<IWorldState>(txProcessingEnv.WorldState)
                    .AddScoped<IReceiptStorage>(NullReceiptStorage.Instance)
                    .AddScoped<IBlockProcessor.IBlockTransactionsExecutor>(ctx =>
                        ctx.ResolveKeyed<IBlockProcessor.IBlockTransactionsExecutor>(IBlockProcessor.IBlockTransactionsExecutor.Rpc)
                    )
                    .AddDecorator<IBlockchainProcessor, OneTimeChainProcessor>()
                    .AddScoped<IRewardCalculator>(NoBlockRewards.Instance)
                    .AddScoped<IBlockValidator>(Always.Valid) // Why?
                    .AddScoped<BlockchainProcessor.Options>(BlockchainProcessor.Options.NoReceipts)
                    .AddScoped<IReadOnlyTxProcessingScope>(txProcessingEnv)
                    .AddScoped<ITracer, Tracer>()
                    ;
            });

            // The tracer need a null receipts while the proof does not
            ILifetimeScope proofRpcScope = rootLifetimeScope.BeginLifetimeScope((builder) =>
            {
                builder.AddSingleton<ITracer>(blockProcessingScope.Resolve<ITracer>());
            });

            proofRpcScope.Disposer.AddInstanceForAsyncDisposal(blockProcessingScope);
            rootLifetimeScope.Disposer.AddInstanceForDisposal(proofRpcScope);

            return proofRpcScope.Resolve<IProofRpcModule>();
        }
    }
}
