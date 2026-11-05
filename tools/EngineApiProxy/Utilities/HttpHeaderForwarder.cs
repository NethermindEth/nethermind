// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EngineApiProxy.Utilities;

/// <summary>
/// Helper for copying per-request HTTP headers (notably Authorization) from a captured client
/// request onto an outgoing <see cref="HttpRequestMessage"/>. Centralises the "skip Content-*,
/// pass Authorization, pass everything else" logic shared by the proxy's forwarders.
/// </summary>
internal static class HttpHeaderForwarder
{
    /// <summary>
    /// Attaches non-content headers from <paramref name="originalHeaders"/> onto
    /// <paramref name="requestMessage"/>. Content-* headers are skipped (HttpClient owns those).
    /// </summary>
    /// <returns><c>true</c> if an Authorization header was attached.</returns>
    public static bool AttachForwardedHeaders(
        HttpRequestMessage requestMessage,
        IReadOnlyDictionary<string, string>? originalHeaders)
    {
        if (originalHeaders is null)
        {
            return false;
        }

        bool authAttached = false;
        foreach (KeyValuePair<string, string> header in originalHeaders)
        {
            if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isAuth = string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase);
            if (isAuth && string.IsNullOrEmpty(header.Value))
            {
                continue;
            }

            requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (isAuth)
            {
                authAttached = true;
            }
        }

        return authAttached;
    }
}
