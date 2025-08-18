// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core.ServiceStopper;
using Nethermind.Facade.Find;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class StartLogIndexService(IBasicApi api, IServiceStopper serviceStopper, ILogIndexService logIndexService) : IStep
    {
        public async Task Execute(CancellationToken cancellationToken)
        {
            await logIndexService.StartAsync();
            api.DisposeStack.Push(logIndexService);
            serviceStopper.AddStoppable(logIndexService);
        }

        public bool MustInitialize => false;
    }
}
