// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Migration;
using Nethermind.State.Pbt.Persistence;
using FlatPersistence = Nethermind.State.Flat.Persistence.IPersistence;

namespace Nethermind.State.Pbt.Steps;

/// <summary>Rebuilds PBT state from a preimage-flat database.</summary>
/// <remarks>
/// The source is scanned into a sorted leaf spool as the EIP-8347 export does, and the spool is then ingested as
/// an imported snapshot is: its logical state staged while its tree is folded.
/// </remarks>
[StepCommand("import-pbt", "Rebuild the PBT state from a preimage-flat database.")]
[RunnerStepDependencies(typeof(InitializeBlockTree), typeof(StartMonitoring))]
public class ImportPbtFromPreimageFlat(
    FlatPersistence flatSource,
    [KeyFilter(DbNames.Code)] IDb codeDb,
    IColumnsDb<PbtColumns> pbtDb,
    PbtRebuilder rebuilder,
    PbtRocksDbPersistence pbtPersistence,
    IDbFactory dbFactory,
    IPbtConfig config,
    ILogManager logManager
) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<ImportPbtFromPreimageFlat>();

    public async Task Execute(CancellationToken cancellationToken)
    {
        if (pbtPersistence.IsValid)
        {
            using IPbtPersistence.IReader pbtReader = pbtPersistence.CreateReader();
            if (_logger.IsInfo) _logger.Info($"PBT state already populated ({pbtReader.CurrentState}); skipping preimage-flat import.");
            return;
        }

        // The one snapshot pins the source for the whole scan.
        using FlatPersistence.IPersistenceReader reader = flatSource.CreateReader();
        if (!reader.IsPreimageMode)
            throw new InvalidConfigurationException(
                "Source flat database is not in preimage mode; addresses and slots cannot be recovered to build PBT.",
                ExitCodes.ForbiddenOptionValue);

        StateId sourceState = reader.CurrentState;
        if (sourceState == StateId.PreGenesis)
        {
            if (_logger.IsInfo) _logger.Info("Source flat database is empty; nothing to import.");
            return;
        }

        int workers = config.ExportConcurrency > 0 ? config.ExportConcurrency : Environment.ProcessorCount;
        if (_logger.IsInfo) _logger.Info($"Rebuilding PBT state from preimage-flat database at {sourceState} with {workers} source reader(s)");

        ClearInterruptedAttempt();
        string directory = Path.Combine(PbtMigrationAnchor.ScratchDirectory(dbFactory, "import"), $"pbt-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using PbtSortedSpool leaves = new("import leaves", directory, config.ExportSortBufferBytes, workers, logManager, cancellationToken)
            { MaxConcurrentPreMerges = workers };
            float leafCount = PbtOfflineSource.Spool("PBT import scan", reader, codeDb, leaves, rawKeys: null, workers, logManager, cancellationToken).Leaves;
            // State is addressed by the source block header's root; the fold records its tree root beside it.
            await PbtLeafIngestion.Ingest(pbtPersistence, rebuilder, _ => PbtLeafIngestion.SpoolLeaves(leaves), read => read / leafCount, workers,
                sourceState, config.ImportWindowSize, expectedRoot: null, static (_, _, _) => { }, logManager, cancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <remarks>
    /// An interrupted import can leave logical entries and trie nodes despite a pre-genesis state pointer.
    /// <see cref="TrieUpdater"/> reads a stored root group before its supplied root hash, so stale nodes
    /// would produce the wrong root.
    /// </remarks>
    private void ClearInterruptedAttempt()
    {
        bool cleared = false;
        pbtDb.GetColumnDb(PbtColumns.Metadata).Remove(PbtRocksDbPersistence.RootNodeGroupKey);

        foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
        {
            if (column == PbtColumns.Metadata) continue;
            IDb columnDb = pbtDb.GetColumnDb(column);
            if (((ISortedKeyValueStore)columnDb).FirstKey is null) continue;
            ((IRangeRemovableKeyValueStore)columnDb).RemoveRange([], PbtColumnSweep.PastEveryKey());
            cleared = true;
        }

        if (cleared && _logger.IsInfo) _logger.Info("Discarded entries left by an interrupted PBT import.");
    }
}
