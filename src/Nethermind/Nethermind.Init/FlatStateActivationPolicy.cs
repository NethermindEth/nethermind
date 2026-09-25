// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
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
/// Validates the FlatDB-only state layout before chain startup begins, and prepares a flat DB that RocksDB
/// auto-repaired (or whose wipe was interrupted) for a state resync.
/// </summary>
/// <remarks>An existing populated FlatDB, or one wiped for a resync, wins over leftover files from an old state
/// database, while a fresh FlatDB refuses to start if those files are detected. This ordering lets operators remove
/// the old database after a successful migration without making every restart fail merely because the old
/// directory still exists.</remarks>
public sealed class FlatStateActivationPolicy
{
    /// <summary>Message shown when a node still points at one of the removed state schemas.</summary>
    public const string LegacySchemaMessage =
        "Hash and HalfPath schemas were deprecated in Nethermind 2.1 and are no longer supported. " +
        "Migrate the database to FlatDB with Nethermind 2.1 and --FlatDb.ImportFromPruningTrieState=true, or start with a fresh FlatDB database. " +
        "See https://docs.nethermind.io/next/fundamentals/configuration/#flatdbimportfrompruningtriestate.";

    private static readonly long LowMemoryLayoutThreshold = 16.GiB;

    public FlatStateActivationPolicy(
        IFlatDbConfig flatDbConfig,
        IInitConfig initConfig,
        ISyncConfig syncConfig,
        IHardwareInfo hardwareInfo,
        Lazy<IColumnsDb<FlatDbColumns>> flatDb,
        IDbFactory dbFactory,
        IFileSystem fileSystem,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(flatDbConfig);
        ArgumentNullException.ThrowIfNull(initConfig);
        ArgumentNullException.ThrowIfNull(syncConfig);
        ArgumentNullException.ThrowIfNull(hardwareInfo);
        ArgumentNullException.ThrowIfNull(flatDb);
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logManager);

        ILogger logger = logManager.GetClassLogger<FlatStateActivationPolicy>();
        ValidateLegacyConfiguration(flatDbConfig, initConfig);
        PrepareFlatState(flatDbConfig, syncConfig, flatDb.Value, dbFactory, fileSystem, logger);
        AdviseLayoutForMemory(flatDbConfig, hardwareInfo, logger);
    }

    internal static void ValidateLegacyConfiguration(IFlatDbConfig flatDbConfig, IInitConfig initConfig)
    {
        if (!flatDbConfig.Enabled)
            throw new InvalidConfigurationException(
                $"FlatDb.Enabled=false is no longer supported. {LegacySchemaMessage}", -1);

        string? configuredKeyScheme = initConfig.StateDbKeyScheme;
        string? keyScheme = configuredKeyScheme?.Trim();
        if (!string.IsNullOrEmpty(keyScheme) && !string.Equals(keyScheme, "Current", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidConfigurationException(
                $"Init.StateDbKeyScheme is unsupported: '{configuredKeyScheme}'. {LegacySchemaMessage}", -1);
        }
    }

    private static void PrepareFlatState(
        IFlatDbConfig flatDbConfig,
        ISyncConfig syncConfig,
        IColumnsDb<FlatDbColumns> db,
        IDbFactory dbFactory,
        IFileSystem fileSystem,
        ILogger logger)
    {
        // The metadata is read off the columns DB rather than through IPersistence: constructing a persistence latches
        // its slot encoding from the pre-wipe DB, which a repair that dropped the metadata would resolve as raw.
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        bool flatHasData = BasePersistence.ReadCurrentState(metadata) != StateId.PreGenesis;
        // A DB wiped for a resync holds no state pointer until the sync completes; it still owns the node's state.
        bool wipedForSync = BasePersistence.ReadWipedForSync(metadata);

        bool wipe = false;
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
            // The data columns are checked too, since the repair may have dropped the state pointer itself.
            bool flatHeldState = flatHasData || wipedForSync || HasAnyDataKey(db);
            if (!flatHeldState)
            {
                if (logger.IsWarn)
                    logger.Warn("Flat DB was auto-repaired by RocksDB but holds no state; the repair is acknowledged.");
                db.AcknowledgeRepair();
            }
            else if (flatDbConfig.OnRepair == FlatDbOnRepair.Resync)
            {
                if (logger.IsError)
                    logger.Error("Flat DB was auto-repaired by RocksDB; the flat state will be wiped and state-synced again (FlatDb.OnRepair=Resync).");
                wipe = true;
            }
            else
            {
                if (logger.IsError)
                    logger.Error("Flat DB was auto-repaired by RocksDB; keeping repaired data (FlatDb.OnRepair=Ignore). This node may diverge.");
                db.AcknowledgeRepair();
            }
        }

        bool existingFlat = flatHasData && !wipe;
        if (existingFlat)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (existing flat DB detected).");
        }
        else if (wipedForSync && !wipe)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (resuming the state sync after a flat DB wipe).");
        }
        else if (!wipe)
        {
            string statePath = dbFactory.GetFullDbPath(new DbSettings("State", DbNames.State));
            if (ContainsLegacyState(fileSystem, statePath))
            {
                throw new InvalidConfigurationException(
                    $"Legacy state database files detected at '{statePath}'. {LegacySchemaMessage}", -1);
            }

            if (logger.IsInfo) logger.Info("State backend: flat (fresh node, flat DB enabled).");
        }

        // FlatDB is the only backend, so a fast-sync configuration without snap sync cannot fall back to patricia:
        // TreeSync (the legacy state sync) is kept for chains whose peers do not serve snap, and warned about loudly.
        if (syncConfig.FastSync && !syncConfig.SnapSync && logger.IsWarn)
        {
            logger.Warn(existingFlat
                ? "FlatDb with FastSync and SnapSync=false is unsafe for a new state sync. This node already has flat state and will keep it. Set Sync.SnapSync=true."
                : "FlatDb with FastSync and SnapSync=false state-syncs through the legacy TreeSync, which can leave permanent holes in the flat state after a restart during the sync (HeaderGasUsedMismatch). Set Sync.SnapSync=true where peers serve snap, and avoid restarting the node until the state sync finishes.");
        }

        if (wipe) WipeForResync(db, flatDbConfig, logger);
    }

    internal static bool ContainsLegacyState(IFileSystem fileSystem, string statePath)
    {
        if (!fileSystem.Directory.Exists(statePath)) return false;

        return fileSystem.Directory.EnumerateFiles(statePath, "*", SearchOption.AllDirectories)
            .Any(static path =>
            {
                string name = Path.GetFileName(path);
                return name.Equals("CURRENT", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("IDENTITY", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("LOCK", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("LOG", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("MANIFEST-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("OPTIONS-", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".sst", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
            });
    }

    /// <remarks>
    /// The wipe runs here rather than being left to the snap sync's own clear: an interrupted wipe must be redone
    /// before anything reads the DB. The repair is acknowledged only after the wipe is flushed, so a crash in between
    /// redoes the wipe.
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

    private static void AdviseLayoutForMemory(IFlatDbConfig flatDbConfig, IHardwareInfo hardwareInfo, ILogger logger)
    {
        if (flatDbConfig.Layout == FlatLayout.FlatInTrie) return;
        if (hardwareInfo.AvailableMemoryBytes >= LowMemoryLayoutThreshold) return;
        if (!logger.IsWarn) return;

        logger.Warn(
            $"Detected {hardwareInfo.AvailableMemoryBytes / 1.GiB} GB of available memory while running FlatDB with the '{flatDbConfig.Layout}' layout. " +
            $"The '{nameof(FlatLayout.FlatInTrie)}' layout is recommended for machines with less than {LowMemoryLayoutThreshold / 1.GiB} GB of RAM. " +
            $"Set '--FlatDb.Layout {nameof(FlatLayout.FlatInTrie)}' to switch (requires a fresh FlatDB sync).");
    }
}
