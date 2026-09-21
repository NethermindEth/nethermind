// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if DEBUG
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// Shared reporting for the DEBUG-only finalizers that flag pooled EVM objects rented and never returned.
/// </summary>
/// <remarks>
/// A leak is always reported; only the site that rented the instance is opt-in, because capturing it costs a
/// stack walk on every rented call frame. Set <c>NETHERMIND_EVM_LEAK_SITES=1</c> to get sites. The switch is
/// mutable so a test can turn capture on for its own probe.
/// </remarks>
internal static class PooledObjectLeakDetector
{
    private const string SitesVariable = "NETHERMIND_EVM_LEAK_SITES";

    internal static bool CaptureSites = Environment.GetEnvironmentVariable(SitesVariable) is "1" or "true";

    /// <summary>Captures the calling frame's stack, or <c>null</c> when site capture is off.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static StackTrace? RentSite() => CaptureSites ? new StackTrace(skipFrames: 1) : null;

    internal static void Report(string type, StackTrace? rentSite) => Console.Error.WriteLine(
        $"Warning: {type} was not disposed. Rented at: {rentSite?.ToString() ?? $"<unknown, set {SitesVariable}=1>"}");
}
#endif
