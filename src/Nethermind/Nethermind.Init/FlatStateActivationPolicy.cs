// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Autofac.Features.AttributeFilters;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init;

internal sealed class FlatStateActivationPolicy(
    IFlatDbConfig flatDbConfig,
    Lazy<IPersistence> flatPersistence,
    [KeyFilter(DbNames.State)] Lazy<IDb> patriciaStateDb,
    ILogManager logManager)
{
    private readonly bool _result = Compute(flatDbConfig, flatPersistence, patriciaStateDb, logManager.GetClassLogger<FlatStateActivationPolicy>());

    public bool ShouldTurnOnFlatDb() => _result;

    private static bool Compute(IFlatDbConfig flatDbConfig, Lazy<IPersistence> flatPersistence, Lazy<IDb> patriciaStateDb, ILogger logger)
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
