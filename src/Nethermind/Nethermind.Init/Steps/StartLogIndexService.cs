// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Facade.Find;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitDatabase), typeof(StartBlockProcessor))]
    public class StartLogIndexService(IBasicApi api, IServiceStopper serviceStopper, ILogIndexService logIndexService) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            if (api.Config<ILogIndexConfig>().Enabled)
            {
                _ = logIndexService.StartAsync();
                serviceStopper.AddStoppable(logIndexService);
            }

            api.DisposeStack.Push(logIndexService);
            return Task.CompletedTask;
        }

        public bool MustInitialize => false;
    }
}
