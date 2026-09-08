// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitTxTypesAndRlp), typeof(SetupKeyStore))]
    public class InitializeBlockTree(
        IInitConfig initConfig,
        IBlockTree blockTree,
        IProcessExitSource processExitSource,
        ILogManager logManager) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            if (initConfig.ExitOnBlockNumber is not null)
            {
                new ExitOnBlockNumberHandler(blockTree, processExitSource, initConfig.ExitOnBlockNumber.Value, logManager);
            }

            return Task.CompletedTask;
        }
    }
}
