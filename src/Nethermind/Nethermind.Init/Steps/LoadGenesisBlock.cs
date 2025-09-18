// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(InitializeBlockchain), typeof(InitializePlugins))]
    public class LoadGenesisBlock(IMainProcessingContext mainProcessingContext, IBlockTree blockTree, IInitConfig initConfig, ILogManager logManager) : IStep
    {
        private readonly ILogger _logger = logManager.GetClassLogger();

        public async Task Execute(CancellationToken cancellationToken)
        {
            // if we already have a database with blocks then we do not need to load genesis from spec
            if (blockTree.Genesis is null)
            {
                mainProcessingContext.GenesisLoader.Load();
            }

            if (!initConfig.ProcessingEnabled)
            {
                if (_logger.IsWarn) _logger.Warn($"Shutting down the blockchain processor due to {nameof(InitConfig)}.{nameof(InitConfig.ProcessingEnabled)} set to false");
                await (mainProcessingContext!.BlockchainProcessor?.StopAsync() ?? Task.CompletedTask);
            }
        }
    }
}
