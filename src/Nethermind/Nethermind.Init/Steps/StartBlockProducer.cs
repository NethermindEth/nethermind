// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Logging;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(InitializeBlockProducer), typeof(ReviewBlockTree),
        typeof(InitializePrecompiles))] // Unfortunately EngineRPC API need review blockTree
    public class StartBlockProducer : IStep
    {
        private readonly IApiWithBlockchain _api;

        public StartBlockProducer(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            if (_api.BlockProductionPolicy!.ShouldStartBlockProduction() && _api.BlockProducer is not null)
            {
                if (_api.BlockTree is null) throw new StepDependencyException(nameof(_api.BlockTree));

                ILogger logger = _api.LogManager.GetClassLogger();
                if (logger.IsInfo) logger.Info($"Starting {_api.SealEngineType} block producer & sealer");
                ProducedBlockSuggester suggester = new(_api.BlockTree, _api.BlockProducer);
                _api.DisposeStack.Push(suggester);
                await _api.BlockProducer.Start();
            }
        }
    }
}
