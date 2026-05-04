// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Nethermind.Mcp.Hosting;

/// <summary>
/// ASP.NET Core middleware that enforces an opt-in <c>Authorization: Bearer &lt;key&gt;</c>
/// requirement on every request when <see cref="IMcpConfig.ApiKey"/> is set. When the key
/// is unset, requests pass through unchanged so MCP can be used unauthenticated on a
/// localhost binding. When set, any deviation from the expected header — including a
/// missing header, malformed value, mismatched scheme, or non-matching token — yields a
/// 401 with an empty body and the next delegate is not invoked. The token comparison
/// uses <see cref="CryptographicOperations.FixedTimeEquals(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte})"/>
/// to mitigate timing-based side channels; secret values are never logged.
/// </summary>
public sealed class ApiKeyAuthMiddleware(RequestDelegate next, IMcpConfig config)
{
    private const string Scheme = "Bearer";

    public async Task InvokeAsync(HttpContext context)
    {
        string? expected = config.ApiKey;
        if (string.IsNullOrEmpty(expected))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("Authorization", out StringValues values) || values.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        string? header = values[0];
        if (string.IsNullOrEmpty(header))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        int spaceIndex = header.IndexOf(' ');
        if (spaceIndex <= 0)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        ReadOnlySpan<char> scheme = header.AsSpan(0, spaceIndex);
        ReadOnlySpan<char> token = header.AsSpan(spaceIndex + 1).Trim();

        if (!scheme.Equals(Scheme.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Constant-time comparison on UTF-8 bytes to prevent timing attacks.
        // FixedTimeEquals returns false for differing lengths without leaking which is longer.
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token.ToString());
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, tokenBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }
}
