// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.SystemClock;

namespace Nethermind.Tools.Kute.Auth;

public sealed class TtlAuth(IAuth auth, ISystemClock clock, TimeSpan ttl) : IAuth
{
    private readonly IAuth _auth = auth;
    private readonly ISystemClock _clock = clock;
    private readonly TimeSpan _ttl = ttl;

    private readonly Lock _refreshLock = new();

    private LastAuth? _lastAuth;

    public string AuthToken
    {
        get
        {
            // One instance is shared by every replay worker; without the lock each expiry lets
            // several workers regenerate at once, a signing burst inside the measured window.
            lock (_refreshLock)
            {
                DateTimeOffset currentTime = _clock.UtcNow;
                if (_lastAuth is null || (currentTime - _lastAuth.GeneratedAt) >= _ttl)
                {
                    _lastAuth = new(currentTime, _auth.AuthToken);
                }

                return _lastAuth.Token;
            }
        }
    }

    private record LastAuth(DateTimeOffset GeneratedAt, string Token);
}
