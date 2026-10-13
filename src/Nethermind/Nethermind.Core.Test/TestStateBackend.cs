// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Db;

namespace Nethermind.Core.Test;

/// <summary>
/// The single place the suite-wide state backend selection is read.
/// </summary>
/// <remarks>
/// Flat is the production default, so it is also the test default; set <c>TEST_USE_TRIE=1</c> to run the
/// suite under patricia instead. Every harness (<c>TestBlockchain</c>, the EF fixture bases,
/// <c>TestNethermindModule</c>) reads the selection from here rather than the environment, so a run cannot
/// end up with some fixtures on flat and others on patricia. Fixtures that need a specific backend pin it
/// explicitly instead of consulting the variable.
/// </remarks>
public static class TestStateBackend
{
    public const string UseTrieEnvironmentVariable = "TEST_USE_TRIE";
    public const string FlatBackendEnvironmentVariable = "TEST_FLAT_BACKEND";

    /// <summary>
    /// Whether fixtures that do not pin a backend run under flat.
    /// </summary>
    /// <remarks>
    /// Read on each access rather than cached: Nethermind.Test.Runner sets the variable from its
    /// <c>--triedb</c> option before it collects fixtures.
    /// </remarks>
    public static bool UseFlatDb => Environment.GetEnvironmentVariable(UseTrieEnvironmentVariable) != "1";

    /// <summary>
    /// The flat-state implementation fixtures that do not pin a backend run under: <c>TEST_FLAT_BACKEND</c>, one of
    /// <c>csharp</c>, <c>rustpersistence</c> and <c>rust</c>, or null when it is unset.
    /// </summary>
    /// <remarks>Read on each access, as <see cref="UseFlatDb"/> is.</remarks>
    public static FlatDbBackend? FlatBackend =>
        Environment.GetEnvironmentVariable(FlatBackendEnvironmentVariable) is { Length: > 0 } backend
            ? Enum.Parse<FlatDbBackend>(backend, ignoreCase: true)
            : null;

    /// <summary>
    /// Applies <see cref="FlatBackend"/>, when set, to <paramref name="config"/>, wherever <see cref="UseFlatDb"/> is applied.
    /// </summary>
    /// <remarks>
    /// The Rust backends refuse the persisted-snapshot tier, so it is turned off for them, and the compaction offset
    /// is pinned so both implementations compact at the same blocks.
    /// </remarks>
    /// <returns>The same config, for chaining.</returns>
    public static T ApplyFlatBackend<T>(T config) where T : IFlatDbConfig
    {
        if (FlatBackend is not { } backend) return config;

        config.Backend = backend;
        if (backend == FlatDbBackend.CSharp) return config;

        config.EnableLongFinality = false;
        config.CompactionOffset = 0;
        return config;
    }

    /// <summary>
    /// Whether <paramref name="configs"/> already pins the state backend, in which case the suite-wide
    /// selection must not be stamped over it.
    /// </summary>
    public static bool PinsBackend(IConfig[] configs)
    {
        foreach (IConfig config in configs)
        {
            if (config is IFlatDbConfig) return true;
        }

        return false;
    }

    /// <summary>
    /// Applies <see cref="UseFlatDb"/> to a config provider owned by the test infrastructure, unless
    /// <paramref name="explicitConfigs"/> pins the backend.
    /// </summary>
    /// <returns>The same provider, for chaining.</returns>
    public static T ApplyDefaultBackend<T>(T configProvider, params IConfig[] explicitConfigs)
        where T : IConfigProvider
    {
        if (!PinsBackend(explicitConfigs))
        {
            IFlatDbConfig flatDbConfig = configProvider.GetConfig<IFlatDbConfig>();
            flatDbConfig.Enabled = UseFlatDb;
            ApplyFlatBackend(flatDbConfig);
        }

        return configProvider;
    }
}
