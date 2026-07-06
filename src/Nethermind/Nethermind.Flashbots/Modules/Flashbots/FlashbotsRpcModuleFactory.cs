// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Flashbots.Handlers;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.State.OverridableEnv;

namespace Nethermind.Flashbots.Modules.Flashbots
{
    public class FlashbotsRpcModuleFactory(
        IProcessingEnvBuilder envBuilder,
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
            IEnv env = envBuilder
                .WithOverridableEnv()
                .WithBlockValidationConfiguration()
                .WithReplacedComponent<IReceiptStorage>(NullReceiptStorage.Instance)
                .WithComponent<ValidateSubmissionHandler.ProcessingEnv>()
                .OwnedByParentLifetime()
                .BuildAs<IEnv>();

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

        // The wrapper forwards BuildAndOverride to the resolved env; the built scope is owned by the parent lifetime.
        public interface IEnv : IOverridableEnv<ValidateSubmissionHandler.ProcessingEnv>
        {
        }
    }
}
