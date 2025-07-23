// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Serialization.Json.PubSub;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class StartLogIndexService : IStep
    {
        private readonly IBasicApi _api;
        private readonly ILogIndexService _logIndexService;

        public StartLogIndexService(IBasicApi api, ILogIndexService logIndexService)
        {
            _api = api;
            _logIndexService = logIndexService;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            await _logIndexService.StartAsync();
            // TODO: fix race condition on disposing both service and storage
            _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => _logIndexService.StopAsync()));
        }

        public bool MustInitialize => false;
    }
}
