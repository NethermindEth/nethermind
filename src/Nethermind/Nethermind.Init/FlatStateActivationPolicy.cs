// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Autofac.Features.AttributeFilters;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
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
    ISyncConfig syncConfig,
    IInitConfig initConfig,
    IFileSystem fileSystem,
    ILogManager logManager)
{
    private static readonly long LowMemoryLayoutThreshold = 16.GiB;

    private readonly bool _result = Compute(flatDbConfig, hardwareInfo, flatDb, patriciaStateDb, syncConfig, initConfig, fileSystem, logManager.GetClassLogger<FlatStateActivationPolicy>());

    public bool ShouldTurnOnFlatDb() => _result;

    private static bool Compute(IFlatDbConfig flatDbConfig, IHardwareInfo hardwareInfo, Lazy<IColumnsDb<FlatDbColumns>> flatDb, Lazy<IDb> patriciaStateDb, ISyncConfig syncConfig, IInitConfig initConfig, IFileSystem fileSystem, ILogger logger)
    {
        bool activateFlat = DecideBackend(flatDbConfig, flatDb, patriciaStateDb, syncConfig, initConfig, fileSystem, logger);
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

    private static bool DecideBackend(IFlatDbConfig flatDbConfig, Lazy<IColumnsDb<FlatDbColumns>> flatDb, Lazy<IDb> patriciaStateDb, ISyncConfig syncConfig, IInitConfig initConfig, IFileSystem fileSystem, ILogger logger)
    {
        if (!flatDbConfig.Enabled)
        {
            // Do not open the flat RocksDB here: resolving IPersistence creates column families
            // on patricia-only nodes and can throw on a stale layout. RocksDB writes CURRENT
            // as soon as the DB is opened, including an empty one, so the signal for state
            // worth keeping is an SST file.
            if (HasSstFile(fileSystem, DbNames.Flat.GetApplicationResourcePath(initConfig.BaseDbPath)))
            {
                throw new InvalidConfigurationException(
                    $"Refusing --FlatDb.Enabled=false on an existing flat DB: that would discard the complete flat state and full-resync. Keep FlatDb.Enabled=true, or delete the '{DbNames.Flat}', '{DbNames.FlatHistory}' and 'persistedSnapshot' directories under '{initConfig.BaseDbPath}' to start over on patricia.",
                    -1);
            }

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

        bool wipe = false;
        bool? patriciaHasData = null;
        if (flatHasData && wipedForSync)
        {
            if (logger.IsWarn)
                logger.Warn(db.WasRepairedOnOpen
                    ? "An interrupted flat DB wipe was detected after a RocksDB auto-repair; it will be redone before the state sync."
                    : "An interrupted flat DB wipe was detected; it will be redone before the state sync.");
            wipe = true;
        }
        else if (db.WasRepairedOnOpen)
        {
            // Only resync when flat was the active backend. An unused empty flat DB that RocksDB
            // repaired must not flip a healthy patricia node onto Flat. The data columns are checked too,
            // since the repair may have dropped the state pointer itself.
            patriciaHasData = HasAnyKey(patriciaStateDb.Value);
            bool flatWasActive = flatHasData || wipedForSync || HasAnyDataKey(db) || !patriciaHasData.Value;
            if (flatDbConfig.OnRepair == FlatDbOnRepair.Resync && flatWasActive)
            {
                if (logger.IsError)
                    logger.Error("Flat DB was auto-repaired by RocksDB; the flat state will be wiped and state-synced again (FlatDb.OnRepair=Resync).");
                wipe = true;
            }
            else
            {
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
        }

        bool existingFlat = flatHasData && !wipe;
        bool activateFlat;
        if (wipe)
        {
            activateFlat = true;
        }
        else if (existingFlat)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (existing flat DB detected).");
            activateFlat = true;
        }
        else if (wipedForSync)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (resuming the state sync after a flat DB wipe).");
            activateFlat = true;
        }
        else if (flatDbConfig.ImportFromPruningTrieState)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (importing from patricia trie state).");
            activateFlat = true;
        }
        else if (patriciaHasData ??= HasAnyKey(patriciaStateDb.Value))
        {
            if (logger.IsInfo) logger.Info("State backend: patricia (existing patricia state detected).");
            activateFlat = false;
        }
        else
        {
            if (logger.IsInfo) logger.Info("State backend: flat (fresh node, flat DB enabled).");
            activateFlat = true;
        }

        if (activateFlat
            && syncConfig.FastSync
            && !syncConfig.SnapSync
            && !(flatDbConfig.ImportFromPruningTrieState && (patriciaHasData ??= HasAnyKey(patriciaStateDb.Value))))
        {
            // TreeSync holes only form during state sync. An already-synced flat node can keep serving.
            if (existingFlat)
            {
                if (logger.IsWarn)
                    logger.Warn("FlatDb with FastSync and SnapSync=false is unsupported for new state sync. This node already has flat state and will keep it. Set Sync.SnapSync=true.");
            }
            else
            {
                // Only a fresh datadir can fall back to patricia: the SST probe above refuses Enabled=false on any other.
                string remedy = wipe && flatHasData && !wipedForSync
                    ? "The flat DB needs a resync after a RocksDB auto-repair. Set Sync.SnapSync=true, or FlatDb.OnRepair=Ignore to keep the repaired data."
                    : wipe || wipedForSync
                        ? "The flat DB holds no state to keep and needs a state sync. Set Sync.SnapSync=true."
                        : "Set Sync.SnapSync=true, or FlatDb.Enabled=false to stay on patricia (fresh datadir only).";
                throw new InvalidConfigurationException(
                    $"FlatDb with FastSync requires SnapSync. Legacy TreeSync on Flat leaves permanent holes (HeaderGasUsedMismatch). {remedy}",
                    -1);
            }
        }

        // Refused before the wipe, so a node whose config is fixed wipes on its next start rather than losing its state first.
        if (wipe) WipeForResync(db, flatDbConfig, logger);
        return activateFlat;
    }

    private static bool HasSstFile(IFileSystem fileSystem, string directory)
    {
        try
        {
            return fileSystem.Directory.EnumerateFiles(directory, "*.sst").Any();
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <remarks>
    /// The wipe runs here rather than being left to the snap sync's own clear: an interrupted wipe must be redone
    /// before anything reads the DB, and the patricia import writes over the surviving repaired keys without clearing them.
    /// The repair is acknowledged only after the wipe is flushed, so a crash in between redoes the wipe.
    /// </remarks>
    private static void WipeForResync(IColumnsDb<FlatDbColumns> db, IFlatDbConfig flatDbConfig, ILogger logger)
    {
        BasePersistence.ClearAllColumns(db);
        db.Flush();
        db.AcknowledgeRepair();
        if (logger.IsInfo) logger.Info("Flat DB wiped; the state sync refills it.");
        // A windowed history keeps its watermark across this wipe, so the sync's pivot lands inside the captured window
        // and HistoryWriter.SeedPivot refuses it at FinalizeSync, which cannot be retried in-process.
        if (flatDbConfig.HistoryEnabled && flatDbConfig.IsHistoryWindowed() && logger.IsWarn)
            logger.Warn("The flatHistory DB was not wiped. Stop the node and wipe it too, or the state sync cannot finish.");
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
