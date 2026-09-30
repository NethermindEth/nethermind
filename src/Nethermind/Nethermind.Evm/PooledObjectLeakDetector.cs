// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// DEBUG-only detection of pooled EVM objects that are rented and never returned.
/// </summary>
/// <remarks>
/// Calls are <see cref="ConditionalAttribute"/>-stripped from Release builds, so pooled types carry no fields,
/// finalizer or calls for this. Each instance gets a <see cref="Rental"/> in a weak table; the rental dies with
/// its instance and its finalizer reports the instance if it was still rented. A leak is always reported; the
/// renting site is opt-in via <c>NETHERMIND_EVM_LEAK_SITES=1</c> because capturing it costs a stack walk per
/// rent. The switch is mutable so a test can turn capture on for its own probe.
/// </remarks>
internal static class PooledObjectLeakDetector
{
    private const string SitesVariable = "NETHERMIND_EVM_LEAK_SITES";

    private static readonly ConditionalWeakTable<object, Rental> _rentals = [];

    internal static bool CaptureSites = Environment.GetEnvironmentVariable(SitesVariable) is { } value
        && (value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Marks <paramref name="pooled"/> as rented, capturing the caller's stack if site capture is on.</summary>
    [Conditional("DEBUG"), MethodImpl(MethodImplOptions.NoInlining)]
    internal static void OnRent(object pooled, string type)
    {
        Rental rental = _rentals.GetValue(pooled, static _ => new Rental());
        rental.Type = type;
        rental.Site = CaptureSites ? new StackTrace(skipFrames: 1) : null;
        rental.IsRented = true;
    }

    /// <summary>Marks <paramref name="pooled"/> as returned, so collecting it is not a leak.</summary>
    [Conditional("DEBUG")]
    internal static void OnReturn(object pooled)
    {
        if (_rentals.TryGetValue(pooled, out Rental? rental))
        {
            rental.IsRented = false;
            // Not held past the rental: a pooled instance would otherwise retain its last site indefinitely.
            rental.Site = null;
        }
    }

    private sealed class Rental
    {
        public string? Type;
        public StackTrace? Site;
        public bool IsRented;

        ~Rental()
        {
            if (IsRented)
            {
                Console.Error.WriteLine(
                    $"Warning: {Type} was not disposed. Rented at: {Site?.ToString() ?? $"<unknown, set {SitesVariable}=1>"}");
            }
        }
    }
}
