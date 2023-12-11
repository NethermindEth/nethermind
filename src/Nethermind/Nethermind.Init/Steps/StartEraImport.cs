// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf.WellKnownTypes;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.Logging;
using Nethermind.Synchronization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(LoadGenesisBlock))]
public class StartEraImport : IStep
{
    private readonly INethermindApi _api;
    private readonly ILogger _logger;

    public StartEraImport(INethermindApi api)
    {
        _api = api;
        _logger = api.LogManager.GetClassLogger();
    }
    public async Task Execute(CancellationToken cancellation)
    {
        if (_api.BlockTree is null)
            throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.BlockValidator is null)
            throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.ReceiptStorage is null)
            throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.SpecProvider is null)
            throw new StepDependencyException(nameof(_api.BlockTree));

        ISyncConfig syncConfig = _api.Config<ISyncConfig>();
        if (syncConfig.FastSync || string.IsNullOrEmpty(syncConfig.ImportDirectory))
        {
            return;
        }
        //TODO some guard checks for directory

        //TODO check best known number and compare with era files
        var networkName = BlockchainIds.GetBlockchainName(_api.SpecProvider.NetworkId);
        _logger.Info($"Checking for unimported blocks '{syncConfig.ImportDirectory}'");

        EraStore eraStore = new(EraReader.GetAllEraFiles(syncConfig.ImportDirectory, networkName).ToArray(), _api.FileSystem);
        long bestEpoch = (_api.BlockTree.BestKnownNumber + 1) / EraWriter.MaxEra1Size;
        if (!eraStore.HasEpoch(bestEpoch))
        {
            _logger.Info($"Best known block {_api.BlockTree.BestKnownNumber} is ahead of era1 archives in '{syncConfig.ImportDirectory}'. Skipping import.");
            return;
        }

        EraImport eraImport = new(
            _api.FileSystem,
            _api.BlockTree,
            _api.BlockValidator,
            _api.ReceiptStorage,
            _api.SpecProvider,
            networkName,
            _api.LogManager);
        await eraImport.Import(syncConfig.ImportDirectory, cancellation);
    }
}
