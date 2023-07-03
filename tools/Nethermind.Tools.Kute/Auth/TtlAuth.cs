// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.SystemClock;

namespace Nethermind.Tools.Kute.Auth;

class TtlAuth : IAuth
{
    private readonly IAuth _auth;
    private readonly ISystemClock _clock;
    private readonly int _ttl;

    private LastAuth? _lastAuth;

    public TtlAuth(IAuth auth, ISystemClock clock, int ttl)
    {
        _auth = auth;
        _clock = clock;
        _ttl = ttl;
    }

    public string AuthToken
    {
        get
        {
            long currentTime = _clock.UtcNow.ToUnixTimeSeconds();
            if (_lastAuth is null || Math.Abs(_lastAuth.GeneratedAt - currentTime) >= _ttl)
            {
                _lastAuth = new(currentTime, _auth.AuthToken);
            }

            return _lastAuth.Token;
        }
    }

    private record LastAuth(long GeneratedAt, string Token);
}
