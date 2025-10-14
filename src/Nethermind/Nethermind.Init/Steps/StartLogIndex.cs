// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.ServiceStopper;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitDatabase), typeof(StartBlockProcessor))]
    public class StartLogIndex(IBasicApi api, IServiceStopper serviceStopper, ILogIndexBuilder logIndexBuilder) : IStep
    {
        public Task Execute(CancellationToken cancellationToken)
        {
            if (api.Config<ILogIndexConfig>().Enabled)
            {
                _ = logIndexBuilder.StartAsync();
                serviceStopper.AddStoppable(logIndexBuilder);
            }

            api.DisposeStack.Push(logIndexBuilder);
            return Task.CompletedTask;
        }

        public bool MustInitialize => false;
    }
}
