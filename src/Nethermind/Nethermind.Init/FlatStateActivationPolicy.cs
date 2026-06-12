// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init;

internal sealed class FlatStateActivationPolicy(
    IFlatDbConfig flatDbConfig,
    Lazy<IColumnsDb<FlatDbColumns>> flatDb,
    [KeyFilter(DbNames.State)] Lazy<IDb> patriciaStateDb,
    ILogManager logManager)
{
    private readonly bool _result = Compute(flatDbConfig, flatDb, patriciaStateDb, logManager.GetClassLogger<FlatStateActivationPolicy>());

    public bool ShouldTurnOnFlatDb() => _result;

    private static bool Compute(IFlatDbConfig flatDbConfig, Lazy<IColumnsDb<FlatDbColumns>> flatDb, Lazy<IDb> patriciaStateDb, ILogger logger)
    {
        if (!flatDbConfig.Enabled)
        {
            if (logger.IsInfo) logger.Info("State backend: patricia (flat DB disabled).");
            return false;
        }

        IReadOnlyKeyValueStore metadata = flatDb.Value.GetColumnDb(FlatDbColumns.Metadata);
        StateId currentState = BasePersistence.ReadCurrentState(metadata);
        FlatLayout? storedLayout = BasePersistence.ReadLayout(metadata);
        if (flatDbConfig.Layout == FlatLayout.PaprikaFlat)
        {
            if (currentState != StateId.PreGenesis && storedLayout == FlatLayout.PaprikaFlat)
            {
                if (logger.IsInfo) logger.Info("State backend: flat (layout PaprikaFlat, existing flat DB detected).");
                return true;
            }
            if (PaprikaFlatPersistence.ReadPendingCurrentState(metadata) is not null)
            {
                if (logger.IsInfo) logger.Info("State backend: flat (layout PaprikaFlat, pending state detected).");
                return true;
            }

            throw new InvalidConfigurationException(
                "FlatDb.Layout=PaprikaFlat requires a prepared PaprikaFlat flat DB. Import, snap tree sync, and fresh flat sync are not supported for PaprikaFlat yet.",
                -1);
        }
        if (currentState != StateId.PreGenesis)
        {
            if (logger.IsInfo) logger.Info($"State backend: flat (layout {storedLayout?.ToString() ?? flatDbConfig.Layout.ToString()}, existing flat DB detected).");
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
