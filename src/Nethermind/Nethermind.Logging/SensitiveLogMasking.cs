// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Logging;

/// <summary>
/// Controls masking of interpolated log values marked with the <c>hide</c> format.
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

    /// <summary>Hides URL credentials, query data, and fragments, or the entire URL when masking is enabled.</summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? string.Empty;
        if (Enabled) return "[redacted]";
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? SafeUrl(uri) : "[redacted]";
    }

    /// <inheritdoc cref="SafeUrl(string?)"/>
    public static string SafeUrl(Uri? url) =>
        url is null ? string.Empty : Enabled || !url.IsAbsoluteUri
            ? "[redacted]"
            : url.UserInfo.Length == 0
                ? url.GetLeftPart(UriPartial.Path)
                : url.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
}
