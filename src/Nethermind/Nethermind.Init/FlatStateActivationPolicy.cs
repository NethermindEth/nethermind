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
    Lazy<IPersistence> flatPersistence,
    [KeyFilter(DbNames.State)] Lazy<IDb> patriciaStateDb,
    ISyncConfig syncConfig,
    IInitConfig initConfig,
    IFileSystem fileSystem,
    ILogManager logManager)
{
    private static readonly long LowMemoryLayoutThreshold = 16.GiB;

    private readonly bool _result = Compute(flatDbConfig, hardwareInfo, flatPersistence, patriciaStateDb, syncConfig, initConfig, fileSystem, logManager.GetClassLogger<FlatStateActivationPolicy>());

    public bool ShouldTurnOnFlatDb() => _result;

    private static bool Compute(IFlatDbConfig flatDbConfig, IHardwareInfo hardwareInfo, Lazy<IPersistence> flatPersistence, Lazy<IDb> patriciaStateDb, ISyncConfig syncConfig, IInitConfig initConfig, IFileSystem fileSystem, ILogger logger)
    {
        bool activateFlat = DecideBackend(flatDbConfig, flatPersistence, patriciaStateDb, syncConfig, initConfig, fileSystem, logger);
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

    private static bool DecideBackend(IFlatDbConfig flatDbConfig, Lazy<IPersistence> flatPersistence, Lazy<IDb> patriciaStateDb, ISyncConfig syncConfig, IInitConfig initConfig, IFileSystem fileSystem, ILogger logger)
    {
        if (!flatDbConfig.Enabled)
        {
            // Probe the directory instead of opening the flat RocksDB: resolving IPersistence
            // creates column families on patricia-only nodes and can throw on a stale layout.
            if (fileSystem.Directory.Exists(Path.Combine(initConfig.BaseDbPath, DbNames.Flat)))
            {
                throw new InvalidConfigurationException(
                    "Refusing --FlatDb.Enabled=false on an existing flat DB: that would discard the complete flat state and full-resync. Keep FlatDb.Enabled=true, or start from a fresh datadir.",
                    -1);
            }

            if (logger.IsInfo) logger.Info("State backend: patricia (flat DB disabled).");
            return false;
        }

        using IPersistence.IPersistenceReader reader = flatPersistence.Value.CreateReader();
        bool existingFlat = reader.CurrentState != StateId.PreGenesis;

        bool activateFlat;
        if (existingFlat)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (existing flat DB detected).");
            activateFlat = true;
        }
        else if (flatDbConfig.ImportFromPruningTrieState)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (importing from patricia trie state).");
            activateFlat = true;
        }
        else if (patriciaStateDb.Value.GetAllKeys().Any())
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
            && !flatDbConfig.ImportFromPruningTrieState)
        {
            // TreeSync holes only form during state sync. An already-synced flat node can keep serving.
            if (existingFlat)
            {
                if (logger.IsWarn)
                    logger.Warn("FlatDb with FastSync and SnapSync=false is unsupported for new state sync. This node already has flat state and will keep it. Set Sync.SnapSync=true.");
            }
            else
            {
                throw new InvalidConfigurationException(
                    "FlatDb with FastSync requires SnapSync. Legacy TreeSync on Flat leaves permanent holes (HeaderGasUsedMismatch). Set Sync.SnapSync=true, or FlatDb.Enabled=false to stay on patricia (fresh datadir only).",
                    -1);
            }
        }

        return activateFlat;
    }
}
