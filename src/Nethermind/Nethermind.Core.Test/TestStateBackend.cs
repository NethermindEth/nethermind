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

    /// <summary>
    /// Whether fixtures that do not pin a backend run under flat.
    /// </summary>
    /// <remarks>
    /// Read on each access rather than cached: Nethermind.Test.Runner sets the variable from its
    /// <c>--triedb</c> option before it collects fixtures.
    /// </remarks>
    public static bool UseFlatDb => Environment.GetEnvironmentVariable(UseTrieEnvironmentVariable) != "1";

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
            configProvider.GetConfig<IFlatDbConfig>().Enabled = UseFlatDb;
        }

        return configProvider;
    }
}
