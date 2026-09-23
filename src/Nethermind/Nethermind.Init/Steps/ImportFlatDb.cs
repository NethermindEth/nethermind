// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.Init.Steps;

[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class ImportFlatDb(
    IBlockTree blockTree,
    IPersistence persistence,
    INodeStorage nodeStorage,
    Importer importer,
    IProcessExitSource exitSource,
    IFlatDbConfig flatDbConfig,
    ILogManager logManager
) : IStep
{
    ILogger _logger = logManager.GetClassLogger<ImportFlatDb>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        // Validate that we're not using a preimage layout
        if (flatDbConfig.Layout is FlatLayout.PreimageFlatV1 or FlatLayout.PreimageFlat)
        {
            if (_logger.IsError) _logger.Error($"Cannot import with FlatLayout.{flatDbConfig.Layout}. Use FlatLayout.Flat or FlatLayout.FlatInTrie instead.");
            if (_logger.IsError) _logger.Error("Preimage mode does not support importing from trie state because the importer uses hash-based raw operations.");
            exitSource.Exit(1);
            return;
        }

        BlockHeader? head = blockTree.Head?.Header;
        if (head is null) return;

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            if (_logger.IsWarn) _logger.Warn($"Current state is {reader.CurrentState}");
            if (reader.CurrentState != StateId.PreGenesis)
            {
                if (_logger.IsInfo) _logger.Info("Flat db already exist");
                return;
            }
        }

        if (head.StateRoot is null ||
            !nodeStorage.KeyExists(null, TreePath.Empty, new ValueHash256(head.StateRoot.Bytes)))
        {
            if (_logger.IsInfo) _logger.Info(
                $"Pruning trie state does not contain head state root {head.StateRoot}; skipping flat DB import.");
            return;
        }

        if (_logger.IsInfo) _logger.Info($"Copying state {head.ToString(BlockHeader.Format.Short)} with state root {head.StateRoot}");

        try
        {
            await importer.Copy(new StateId(head), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsInfo) _logger.Info("Import cancelled by user");
            exitSource.Exit(1);
            return;
        }

        exitSource.Exit(0);
    }
}
