// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Db;

namespace Nethermind.Core.Test;

/// <summary>
/// The suite-wide state backend selection for fixtures that do not pin a backend.
/// </summary>
/// <remarks>
/// Tests default to patricia; set <c>TEST_USE_FLAT=1</c> to opt into flat state.
/// Explicit backend configurations take precedence over this selection.
/// </remarks>
public static class TestStateBackend
{
    /// <summary>
    /// The environment variable that enables flat state when set to <c>1</c>.
    /// </summary>
    public const string UseFlatEnvironmentVariable = "TEST_USE_FLAT";

    /// <summary>
    /// Whether fixtures that do not pin a backend run under flat.
    /// </summary>
    /// <remarks>
    /// Read on each access because Nethermind.Test.Runner sets the variable from its
    /// <c>--flatdb</c> option before collecting fixtures.
    /// </remarks>
    public static bool UseFlatDb => Environment.GetEnvironmentVariable(UseFlatEnvironmentVariable) == "1";

    /// <summary>
    /// Whether <paramref name="configs"/> already pins the state backend.
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
    /// Applies <see cref="UseFlatDb"/> to a provider owned by the test infrastructure,
    /// unless <paramref name="explicitConfigs"/> pins the backend.
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
