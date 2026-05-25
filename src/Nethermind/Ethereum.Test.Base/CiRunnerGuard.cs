// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>
/// Skips heavy tests in CI on runners that are too slow or running variant builds.
/// Local runs always execute. Set TEST_SKIP_HEAVY=1 in CI for checked/no-intrinsics variants.
/// </summary>
public static class CiRunnerGuard
{
    private static readonly bool s_isCi = IsCi();
    private static readonly bool s_isLinuxX64 = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static readonly bool s_skipHeavy = Environment.GetEnvironmentVariable("TEST_SKIP_HEAVY") == "1";

    /// <summary>
    /// Skips in CI on non-Linux-x64 runners. Local macOS/Windows runs are always allowed.
    /// Use for standard pyspec tests that are fast enough outside Linux x64 CI.
    /// </summary>
    public static void SkipIfNotLinuxX64Ci()
    {
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI - Pyspec generated fixture shards only run on Linux x64 runners");
    }

    /// <summary>
    /// Skips everywhere in CI except Linux x64, and also honours TEST_SKIP_HEAVY=1.
    /// Use for engine/Amsterdam/ZkEvm tests that carry a large job-time budget.
    /// </summary>
    public static void SkipIfNotLinuxX64()
    {
        if (s_isCi && s_skipHeavy)
            Assert.Ignore("Skipped - TEST_SKIP_HEAVY is set");
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI - engine/Amsterdam/ZkEvm tests only run on Linux x64");
    }

    private static bool IsCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
}
