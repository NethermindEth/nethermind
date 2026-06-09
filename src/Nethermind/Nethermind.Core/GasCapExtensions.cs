// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Helpers for JSON-RPC <c>GasCap</c>: <see langword="null"/> or <c>0</c> means no cap.
/// </summary>
public static class GasCapExtensions
{
    /// <summary>Whether <paramref name="gasCap"/> applies a finite RPC gas limit.</summary>
    public static bool IsGasCapped(this long? gasCap) => gasCap is not null and not 0;

    /// <summary>Returns <paramref name="gasCap"/> when capped, otherwise <see cref="long.MaxValue"/>.</summary>
    public static long EffectiveGasCap(this long? gasCap) => gasCap.IsGasCapped() ? gasCap!.Value : long.MaxValue;
}
