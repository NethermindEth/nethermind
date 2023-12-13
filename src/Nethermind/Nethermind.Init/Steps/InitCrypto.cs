// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core.Attributes;
using Nethermind.Crypto;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitRlp))]
    public class InitCrypto : IStep
    {
        private readonly IBasicApi _api;

        public InitCrypto(INethermindApi api)
        {
            _api = api;
        }

        [Todo(Improve.Refactor, "Automatically scan all the references solutions?")]
        public virtual Task Execute(CancellationToken _)
        {
            _api.EthereumEcdsa = new EthereumEcdsa(_api.SpecProvider!.ChainId, _api.LogManager);
            return Task.CompletedTask;
        }
    }
}
