// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Auth;
using Nethermind.Tools.Kute.SecretProvider;
using Nethermind.Tools.Kute.SystemClock;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>Builds the optional bearer-token provider for a replay run.</summary>
/// <remarks>
/// State-reading traces are replayed against the unauthenticated JSON-RPC port, so a secret is
/// optional here, unlike on the Engine API port.
/// </remarks>
public static class ReplayAuth
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromSeconds(60);

    /// <summary>Creates a token provider, or none when no secret was supplied.</summary>
    /// <param name="secretPath">Path to a hex-encoded JWT secret, or <see langword="null"/>.</param>
    public static IAuth? TryCreate(string? secretPath)
    {
        if (string.IsNullOrEmpty(secretPath))
        {
            return null;
        }

        RealSystemClock clock = new();

        return new TtlAuth(new JwtAuth(clock, new FileSecretProvider(secretPath)), clock, TokenTtl);
    }
}
