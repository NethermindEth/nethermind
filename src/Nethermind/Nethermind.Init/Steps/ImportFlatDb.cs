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
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.Init.Steps;

[StepCommand("import-flat-db", "Copy the pruning-trie state database into the flat state database.")]
[RunnerStepDependencies(typeof(InitializeBlockTree))]
public class ImportFlatDb(
    IBlockTree blockTree,
    IPersistence persistence,
    INodeStorage nodeStorage,
    Importer importer,
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
            throw new InvalidConfigurationException(
                $"Cannot import with FlatLayout.{flatDbConfig.Layout}. Use FlatLayout.Flat or FlatLayout.FlatInTrie instead. " +
                "Preimage mode does not support importing from trie state because the importer uses hash-based raw operations.",
                ExitCodes.ForbiddenOptionValue);
        }

        // Nothing to import is a failure, not a no-op. This step ends the process, so returning quietly would
        // stop the node with exit 0 on every restart once the flag is left in a config after a finished import.
        BlockHeader? head = blockTree.Head?.Header
            ?? throw new InvalidConfigurationException(
                $"Cannot import: the block tree has no head. Remove FlatDb.{nameof(IFlatDbConfig.ImportFromPruningTrieState)} to start the node normally.",
                ExitCodes.ForbiddenOptionValue);

        using (IPersistence.IPersistenceReader reader = persistence.CreateReader())
        {
            if (_logger.IsWarn) _logger.Warn($"Current state is {reader.CurrentState}");
            if (reader.CurrentState != StateId.PreGenesis)
            {
                throw new InvalidConfigurationException(
                    $"Cannot import: the flat DB is already populated at {reader.CurrentState}. Remove FlatDb.{nameof(IFlatDbConfig.ImportFromPruningTrieState)} to start the node normally.",
                    ExitCodes.ForbiddenOptionValue);
            }
        }

        if (head.StateRoot is null ||
            !nodeStorage.KeyExists(null, TreePath.Empty, new ValueHash256(head.StateRoot.Bytes)))
        {
            throw new InvalidConfigurationException(
                $"Cannot import: the pruning trie state does not contain head state root {head.StateRoot}. Remove FlatDb.{nameof(IFlatDbConfig.ImportFromPruningTrieState)} to start the node normally.",
                ExitCodes.ForbiddenOptionValue);
        }

        if (_logger.IsInfo) _logger.Info($"Copying state {head.ToString(BlockHeader.Format.Short)} with state root {head.StateRoot}");

        try
        {
            await importer.Copy(new StateId(head), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown has already set the exit code; report it as the interruption it is rather than letting
            // the step machinery log a failure stack trace.
            if (_logger.IsInfo) _logger.Info("Import cancelled by user");
        }
    }
}
