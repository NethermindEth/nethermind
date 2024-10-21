// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Era1;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(typeof(InitializeBlockchain))]
public class ImportEraStep: IStep
{
    protected readonly IApiWithBlockchain _api;
    private readonly ILogger _logger;
    protected readonly IEraConfig _eraConfig;

    public ImportEraStep(IApiWithBlockchain api)
    {
        _api = api;
        _logger = api.LogManager.GetClassLogger<ImportEraStep>();
        _eraConfig = api.Config<IEraConfig>();
    }

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (_api.BlockTree is null)
            throw new StepDependencyException(nameof(_api.BlockTree));
        if (_api.BlockValidator is null)
            throw new StepDependencyException(nameof(_api.BlockValidator));
        if (_api.ReceiptStorage is null)
            throw new StepDependencyException(nameof(_api.ReceiptStorage));
        if (_api.SpecProvider is null)
            throw new StepDependencyException(nameof(_api.SpecProvider));

        if (string.IsNullOrEmpty(_eraConfig.ImportDirectory))
        {
            return;
        }
        if (!_api.FileSystem.Directory.Exists(_eraConfig.ImportDirectory))
        {
            _logger.Warn($"The directory given for import '{_eraConfig.ImportDirectory}' does not exist.");
            return;
        }

        var networkName = BlockchainIds.GetBlockchainName(_api.SpecProvider.NetworkId);
        _logger.Info($"Checking for unimported blocks '{_eraConfig.ImportDirectory}'");

        var eraFiles = EraPathUtils.GetAllEraFiles(_eraConfig.ImportDirectory, networkName).ToArray();
        if (eraFiles.Length == 0)
        {
            _logger.Warn($"No files for '{networkName}' import was found in '{_eraConfig.ImportDirectory}'.");
            return;
        }

        EraImporter eraImport = new(
            _api.FileSystem,
            _api.BlockTree,
            _api.BlockValidator,
            _api.ReceiptStorage,
            _api.SpecProvider,
            _api.LogManager,
            networkName);

        try
        {
            await eraImport.ImportAsArchiveSync(_eraConfig.ImportDirectory, _api.ProcessExit!.Token);
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
        {
            _logger.Warn($"A running import job was cancelled.");
        }
        catch (Exception e) when (e is EraException or EraImportException)
        {
            _logger.Error($"The import failed with the message: {e.Message}");
        }
        catch (Exception e)
        {
            _logger.Error("Import error", e);
            throw;
        }
    }

}
