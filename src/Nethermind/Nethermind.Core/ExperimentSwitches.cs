// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Throwaway environment-variable overrides for the cross-block state-cache experiment sweep.
/// </summary>
/// <remarks>
/// Env vars rather than config items so a single Docker image can drive the whole sweep through the
/// benchmark workflows' <c>client_env</c> input, with no DI or config plumbing to unpick afterwards.
/// NOT for production — this file is expected to be deleted with the branch.
/// </remarks>
public static class ExperimentSwitches
{
    public static int Int(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    public static bool Bool(string name)
        => Environment.GetEnvironmentVariable(name) is "1" or "true" or "True";
}
