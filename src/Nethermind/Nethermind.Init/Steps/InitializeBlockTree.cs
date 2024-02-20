// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp), typeof(InitDatabase), typeof(MigrateConfigs), typeof(SetupKeyStore))]
    public class InitializeBlockTree : IStep
    {
        private readonly IInitConfig _initConfig;
        private readonly IComponentContext _ctx;

        public InitializeBlockTree(IComponentContext ctx, IInitConfig initConfig)
        {
            _ctx = ctx;
            _initConfig = initConfig;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            if (_initConfig.ExitOnBlockNumber != null)
            {
                _ctx.Resolve<ExitOnBlockNumberHandler>();
            }

            return Task.CompletedTask;
        }
    }
}
