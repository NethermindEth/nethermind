// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init;

/// <summary>
/// Resolves whether the node actually runs on the flat state backend, as opposed to patricia. The decision
/// combines the configured preference with the on-disk state (existing flat/patricia DBs, import settings).
/// </summary>
public sealed class FlatStateActivationPolicy(
    IFlatDbConfig flatDbConfig,
    IHardwareInfo hardwareInfo,
    Lazy<IColumnsDb<FlatDbColumns>> flatDb,
    [KeyFilter(DbNames.State)] Lazy<IDb> patriciaStateDb,
    ILogManager logManager)
{
    private static readonly long LowMemoryLayoutThreshold = 16.GiB;

    private readonly bool _result = Compute(flatDbConfig, hardwareInfo, flatDb, patriciaStateDb, logManager.GetClassLogger<FlatStateActivationPolicy>());

    public bool ShouldTurnOnFlatDb() => _result;

    private static bool Compute(IFlatDbConfig flatDbConfig, IHardwareInfo hardwareInfo, Lazy<IColumnsDb<FlatDbColumns>> flatDb, Lazy<IDb> patriciaStateDb, ILogger logger)
    {
        bool activateFlat = DecideBackend(flatDbConfig, flatDb, patriciaStateDb, logger);
        if (activateFlat) AdviseLayoutForMemory(flatDbConfig, hardwareInfo, logger);
        return activateFlat;
    }

    private static void AdviseLayoutForMemory(IFlatDbConfig flatDbConfig, IHardwareInfo hardwareInfo, ILogger logger)
    {
        if (flatDbConfig.Layout == FlatLayout.FlatInTrie) return;
        if (hardwareInfo.AvailableMemoryBytes >= LowMemoryLayoutThreshold) return;
        if (!logger.IsWarn) return;

        logger.Warn(
            $"Detected {hardwareInfo.AvailableMemoryBytes / 1.GiB} GB of available memory while running the flat DB with the '{flatDbConfig.Layout}' layout. " +
            $"The '{nameof(FlatLayout.FlatInTrie)}' layout is recommended for machines with less than {LowMemoryLayoutThreshold / 1.GiB} GB of RAM. " +
            $"Set '--FlatDb.Layout {nameof(FlatLayout.FlatInTrie)}' to switch (requires a fresh flat DB sync).");
    }

    private static bool DecideBackend(IFlatDbConfig flatDbConfig, Lazy<IColumnsDb<FlatDbColumns>> flatDb, Lazy<IDb> patriciaStateDb, ILogger logger)
    {
        if (!flatDbConfig.Enabled)
        {
            if (logger.IsInfo) logger.Info("State backend: patricia (flat DB disabled).");
            return false;
        }

        // The metadata is read off the columns DB rather than through IPersistence: constructing a persistence latches
        // its slot encoding from the pre-wipe DB, which a repair that dropped the metadata would resolve as raw.
        IColumnsDb<FlatDbColumns> db = flatDb.Value;
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        bool flatHasData = BasePersistence.ReadCurrentState(metadata) != StateId.PreGenesis;
        // A DB wiped for a resync holds no state pointer until the sync completes; it is still the active backend.
        bool wipedForSync = BasePersistence.ReadWipedForSync(metadata);

        if (flatHasData && wipedForSync)
        {
            if (logger.IsWarn)
                logger.Warn(db.WasRepairedOnOpen
                    ? "An interrupted flat DB wipe was detected after a RocksDB auto-repair; redoing it before the state sync."
                    : "An interrupted flat DB wipe was detected; redoing it before the state sync.");
            WipeForResync(db, logger);
            return true;
        }

        bool? patriciaHasData = null;
        if (db.WasRepairedOnOpen)
        {
            // Only resync when flat was the active backend. An unused empty flat DB that RocksDB
            // repaired must not flip a healthy patricia node onto Flat. The data columns are checked too,
            // since the repair may have dropped the state pointer itself.
            patriciaHasData = HasAnyKey(patriciaStateDb.Value);
            bool flatWasActive = flatHasData || wipedForSync || HasAnyDataKey(db) || !patriciaHasData.Value;
            if (flatDbConfig.OnRepair == FlatDbOnRepair.Resync && flatWasActive)
            {
                if (logger.IsError)
                    logger.Error("Flat DB was auto-repaired by RocksDB; wiping flat state and re-entering state sync (FlatDb.OnRepair=Resync).");
                WipeForResync(db, logger);
                return true;
            }

            if (flatWasActive)
            {
                if (logger.IsError)
                    logger.Error("Flat DB was auto-repaired by RocksDB; keeping repaired data (FlatDb.OnRepair=Ignore). This node may diverge.");
            }
            else if (logger.IsWarn)
            {
                logger.Warn("Flat DB was auto-repaired by RocksDB but holds no state; the patricia backend stays active and the repair is acknowledged.");
            }
            db.AcknowledgeRepair();
        }

        if (flatHasData)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (existing flat DB detected).");
            return true;
        }
        if (wipedForSync)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (resuming the state sync after a flat DB wipe).");
            return true;
        }
        if (flatDbConfig.ImportFromPruningTrieState)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (importing from patricia trie state).");
            return true;
        }
        if (patriciaHasData ?? HasAnyKey(patriciaStateDb.Value))
        {
            if (logger.IsInfo) logger.Info("State backend: patricia (existing patricia state detected).");
            return false;
        }
        if (logger.IsInfo) logger.Info("State backend: flat (fresh node, flat DB enabled).");
        return true;
    }

    /// <remarks>
    /// The wipe runs here rather than being left to snap sync, because a tree-only state sync
    /// (<c>Sync.SnapSync=false</c>) never wipes and would skip every subtree whose surviving root node still hashes.
    /// The repair is acknowledged only after the wipe is flushed, so a crash in between redoes the wipe.
    /// </remarks>
    private static void WipeForResync(IColumnsDb<FlatDbColumns> db, ILogger logger)
    {
        BasePersistence.ClearAllColumns(db);
        db.Flush();
        db.AcknowledgeRepair();
        if (logger.IsInfo) logger.Info("Flat DB wiped; the state sync refills it.");
    }

    private static bool HasAnyDataKey(IColumnsDb<FlatDbColumns> db)
    {
        foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
        {
            if (column != FlatDbColumns.Metadata && HasAnyKey(db.GetColumnDb(column))) return true;
        }

        return false;
    }

    private static bool HasAnyKey(IDb db)
    {
        foreach (byte[] _ in db.GetAllKeys()) return true;
        return false;
    }
}
