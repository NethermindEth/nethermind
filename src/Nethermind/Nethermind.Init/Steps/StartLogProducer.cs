// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Serialization.Json.PubSub;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor))]
    public class StartLogProducer : IStep
    {
        private readonly INethermindApi _api;

        public StartLogProducer(INethermindApi api)
        {
            _api = api;
        }

        public Task Execute(CancellationToken cancellationToken)
        {
            // TODO: this should be configure in init maybe?
            LogPublisher logPublisher = new LogPublisher(_api.EthereumJsonSerializer!, _api.LogManager);
            _api.Publishers.Add(logPublisher);
            return Task.CompletedTask;
        }

        public bool MustInitialize => false;
    }
}
