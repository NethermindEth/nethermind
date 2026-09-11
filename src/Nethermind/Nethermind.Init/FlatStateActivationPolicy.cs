// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.Init;

/// <summary>
/// Validates the FlatDB-only state layout before chain startup opens state databases.
/// </summary>
/// <remarks>An existing populated FlatDB wins over leftover files from an old state database, while a fresh FlatDB
/// refuses to start if those files are detected. This ordering lets operators remove the old database after a
/// successful migration without making every restart fail merely because the old directory still exists.</remarks>
public sealed class FlatStateActivationPolicy
{
    /// <summary>Message shown when a node still points at one of the removed state schemas.</summary>
    public const string LegacySchemaMessage =
        "Hash and HalfPath schemas were deprecated in Nethermind 2.1 and are no longer supported. " +
        "Use Nethermind 2.1 to open/migrate the database to FlatDB, or start with a fresh FlatDB database. " +
        "See https://docs.nethermind.io/next/fundamentals/configuration/#flatdbimportfrompruningtriestate.";

    private static readonly long LowMemoryLayoutThreshold = 16.GiB;

    private readonly bool _result;

    public FlatStateActivationPolicy(
        IFlatDbConfig flatDbConfig,
        IInitConfig initConfig,
        IHardwareInfo hardwareInfo,
        Lazy<IPersistence> flatPersistence,
        IDbFactory dbFactory,
        IFileSystem fileSystem,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(flatDbConfig);
        ArgumentNullException.ThrowIfNull(initConfig);
        ArgumentNullException.ThrowIfNull(hardwareInfo);
        ArgumentNullException.ThrowIfNull(flatPersistence);
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logManager);

        ILogger logger = logManager.GetClassLogger<FlatStateActivationPolicy>();
        ValidateLegacyConfiguration(flatDbConfig, initConfig);

        using IPersistence.IPersistenceReader reader = flatPersistence.Value.CreateReader();
        if (reader.CurrentState != StateId.PreGenesis)
        {
            if (logger.IsInfo) logger.Info("State backend: flat (existing flat DB detected).");
            _result = true;
        }
        else if (ContainsLegacyState(fileSystem, dbFactory.GetFullDbPath(new DbSettings("State", DbNames.State))))
        {
            throw new InvalidConfigurationException(LegacySchemaMessage, -1);
        }
        else
        {
            if (logger.IsInfo) logger.Info("State backend: flat (fresh node, flat DB enabled).");
            _result = true;
        }

        AdviseLayoutForMemory(flatDbConfig, hardwareInfo, logger);
    }

    /// <summary>Returns whether FlatDB passed startup validation.</summary>
    public bool ShouldTurnOnFlatDb() => _result;

    internal static void ValidateLegacyConfiguration(IFlatDbConfig flatDbConfig, IInitConfig initConfig)
    {
        if (!flatDbConfig.Enabled)
            throw new InvalidConfigurationException(LegacySchemaMessage, -1);

        if (flatDbConfig.ImportFromPruningTrieState)
            throw new InvalidConfigurationException(LegacySchemaMessage, -1);

        string? keyScheme = initConfig.StateDbKeyScheme?.Trim();
        if (string.Equals(keyScheme, "Hash", StringComparison.OrdinalIgnoreCase)
            || string.Equals(keyScheme, "HalfPath", StringComparison.OrdinalIgnoreCase)
            || keyScheme is "0" or "1")
        {
            throw new InvalidConfigurationException(LegacySchemaMessage, -1);
        }
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
