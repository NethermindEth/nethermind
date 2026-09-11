// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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
    Lazy<IPersistence> flatPersistence,
    [KeyFilter(DbNames.State)] Lazy<IDb> patriciaStateDb,
    ILogManager logManager)
{
    private static readonly long LowMemoryLayoutThreshold = 16.GiB;

    private readonly bool _result = Compute(flatDbConfig, hardwareInfo, flatPersistence, patriciaStateDb, logManager.GetClassLogger<FlatStateActivationPolicy>());

    public bool ShouldTurnOnFlatDb() => _result;

    private static bool Compute(IFlatDbConfig flatDbConfig, IHardwareInfo hardwareInfo, Lazy<IPersistence> flatPersistence, Lazy<IDb> patriciaStateDb, ILogger logger)
    {
        bool activateFlat = DecideBackend(flatDbConfig, flatPersistence, patriciaStateDb, logger);
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

    private static bool DecideBackend(IFlatDbConfig flatDbConfig, Lazy<IPersistence> flatPersistence, Lazy<IDb> patriciaStateDb, ILogger logger)
    {
        if (!flatDbConfig.Enabled)
        {
            if (logger.IsInfo) logger.Info("State backend: patricia (flat DB disabled).");
            return false;
        }
        using IPersistence.IPersistenceReader reader = flatPersistence.Value.CreateReader();
        if (reader.CurrentState != StateId.PreGenesis)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (existing flat DB detected).");
            return true;
        }
        if (flatDbConfig.ImportFromPruningTrieState)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (importing from patricia trie state).");
            return true;
        }
        if (patriciaStateDb.Value.GetAllKeys().Any())
        {
            if (logger.IsInfo) logger.Info("State backend: patricia (existing patricia state detected).");
            return false;
        }
        if (logger.IsInfo) logger.Info("State backend: flat (fresh node, flat DB enabled).");
        return true;
    }
}
