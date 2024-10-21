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
    protected readonly ISyncConfig _syncConfig;

    public ImportEraStep(IApiWithBlockchain api)
    {
        _api = api;
        _logger = api.LogManager.GetClassLogger<ImportEraStep>();
        _syncConfig = api.Config<ISyncConfig>();
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

        ISyncConfig syncConfig = _api.Config<ISyncConfig>();
        if (string.IsNullOrEmpty(syncConfig.ImportDirectory))
        {
            return;
        }
        if (!_api.FileSystem.Directory.Exists(syncConfig.ImportDirectory))
        {
            _logger.Warn($"The directory given for import '{syncConfig.ImportDirectory}' does not exist.");
            return;
        }

        var networkName = BlockchainIds.GetBlockchainName(_api.SpecProvider.NetworkId);
        _logger.Info($"Checking for unimported blocks '{syncConfig.ImportDirectory}'");

        var eraFiles = EraPathUtils.GetAllEraFiles(syncConfig.ImportDirectory, networkName).ToArray();
        if (eraFiles.Length == 0)
        {
            _logger.Warn($"No files for '{networkName}' import was found in '{syncConfig.ImportDirectory}'.");
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
            if (_syncConfig.FastSync)
            {
                return;
            }
            else
            {
                //Import as a full archive
                _logger.Info($"Starting full archive import from '{syncConfig.ImportDirectory}'");
                await eraImport.ImportAsArchiveSync(syncConfig.ImportDirectory, _api.ProcessExit!.Token);
            }
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
