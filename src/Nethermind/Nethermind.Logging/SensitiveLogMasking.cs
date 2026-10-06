// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Logging;

/// <summary>
/// Controls masking of interpolated log values marked with the <c>sensitive</c> format.
/// </summary>
public static class SensitiveLogMasking
{
    private static bool s_enabled;

    /// <summary>Gets or sets whether fields marked as sensitive are masked.</summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref s_enabled);
        set => Volatile.Write(ref s_enabled, value);
    }
}
