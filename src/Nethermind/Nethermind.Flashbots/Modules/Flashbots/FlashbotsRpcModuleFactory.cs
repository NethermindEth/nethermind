// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Flashbots.Handlers;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Flashbots.Modules.Flashbots
{
    public class FlashbotsRpcModuleFactory(
        ILifetimeScope rootLifetime,
        IOverridableEnvFactory overridableEnvFactory,
        IHeaderValidator headerValidator,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        ILogManager logManager,
        ISpecProvider specProvider,
        IFlashbotsConfig flashbotsConfig,
        IEthereumEcdsa ethereumEcdsa,
        IBlockValidationModule[] validationBlockProcessingModules
    ) : ModuleFactoryBase<IFlashbotsRpcModule>
    {
        public override IFlashbotsRpcModule Create()
        {
            IOverridableEnv overridableEnv = overridableEnvFactory.Create();

            ILifetimeScope moduleLifetime = rootLifetime.BeginLifetimeScope((builder) => builder
                .AddModule(validationBlockProcessingModules)
                .AddSingleton<IReceiptStorage>(NullReceiptStorage.Instance)
                .AddScoped<ValidateSubmissionHandler.ProcessingEnv>()
                .AddModule(overridableEnv));

            rootLifetime.Disposer.AddInstanceForAsyncDisposal(moduleLifetime);
            ValidateSubmissionHandler validateSubmissionHandler = new ValidateSubmissionHandler(
                headerValidator,
                blockTree,
                blockValidator,
                moduleLifetime.Resolve<IOverridableEnv<ValidateSubmissionHandler.ProcessingEnv>>(),
                logManager,
                specProvider,
                flashbotsConfig,
                ethereumEcdsa
            );

            return new FlashbotsRpcModule(validateSubmissionHandler);
        }
    }
}
