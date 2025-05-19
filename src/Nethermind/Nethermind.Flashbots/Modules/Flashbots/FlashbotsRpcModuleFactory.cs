// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Flashbots.Handlers;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Flashbots.Modules.Flashbots
{
    public class FlashbotsRpcModuleFactory(
        IHeaderValidator headerValidator,
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        IReadOnlyTxProcessingEnvFactory readOnlyTxProcessingEnvFactory,
        ILogManager logManager,
        ISpecProvider specProvider,
        IFlashbotsConfig flashbotsConfig,
        IEthereumEcdsa ethereumEcdsa
    ) : ModuleFactoryBase<IFlashbotsRpcModule>
    {

        public override IFlashbotsRpcModule Create()
        {
            ValidateSubmissionHandler validateSubmissionHandler = new ValidateSubmissionHandler(
                headerValidator,
                blockTree,
                blockValidator,
                readOnlyTxProcessingEnvFactory,
                logManager,
                specProvider,
                flashbotsConfig,
                ethereumEcdsa
            );

            return new FlashbotsRpcModule(validateSubmissionHandler);
        }
    }
}
