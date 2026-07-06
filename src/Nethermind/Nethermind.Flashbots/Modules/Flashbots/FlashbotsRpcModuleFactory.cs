// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Flashbots.Handlers;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Flashbots.Modules.Flashbots
{
    public class FlashbotsRpcModuleFactory(
        ILifetimeScope rootLifetime,
        IProcessingEnvBuilder envBuilder,
        IOverridableEnvFactory overridableEnvFactory,
        IHeaderValidator headerValidator,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        ILogManager logManager,
        ISpecProvider specProvider,
        IFlashbotsConfig flashbotsConfig,
        IEthereumEcdsa ethereumEcdsa
    ) : ModuleFactoryBase<IFlashbotsRpcModule>
    {
        public override IFlashbotsRpcModule Create()
        {
            IOverridableEnvHandle<ValidateSubmissionHandler.ProcessingEnv> env = envBuilder
                .WithOverridableEnv(overridableEnvFactory.Create())
                .WithBlockValidationConfiguration()
                .WithReplacedComponent<IReceiptStorage>(NullReceiptStorage.Instance)
                .Configure(builder => builder.AddScoped<ValidateSubmissionHandler.ProcessingEnv>())
                .BuildAsOverridableEnv<ValidateSubmissionHandler.ProcessingEnv>();

            rootLifetime.Disposer.AddInstanceForAsyncDisposal(env);
            ValidateSubmissionHandler validateSubmissionHandler = new(
                headerValidator,
                blockTree,
                blockValidator,
                env,
                logManager,
                specProvider,
                flashbotsConfig,
                ethereumEcdsa
            );

            return new FlashbotsRpcModule(validateSubmissionHandler);
        }
    }
}
