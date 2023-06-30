// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.SystemClock;

namespace Nethermind.Tools.Kute.Auth;

class ExpiringCachedAuth : IAuth
{
    private readonly IAuth _auth;
    private readonly ISystemClock _clock;
    private readonly uint _ttl;

    private LastAuth? _lastAuth;

    public ExpiringCachedAuth(IAuth auth, ISystemClock clock, uint ttl)
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

    private class LastAuth
    {
        public long GeneratedAt { get; }
        public string Token { get; }

        public LastAuth(long generatedAt, string token)
        {
            GeneratedAt = generatedAt;
            Token = token;
        }
    }
}
