// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Nethermind.Tools.Kute.SecretProvider;
using Nethermind.Tools.Kute.SystemClock;

namespace Nethermind.Tools.Kute.Auth;

class JwtAuth : IAuth
{
    private readonly byte[] _secret;
    private readonly ISystemClock _clock;

    public string AuthToken
    {
        get => GenerateAuthToken();
    }

    public JwtAuth(ISystemClock clock, ISecretProvider secretProvider)
    {
        _clock = clock;

        var hexSecret = secretProvider.Secret;
        _secret = Enumerable.Range(0, hexSecret.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hexSecret.Substring(x, 2), 16))
            .ToArray();
    }

    private string GenerateAuthToken()
    {
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };

        return handler.CreateToken(new SecurityTokenDescriptor
        {
            IssuedAt = _clock.UtcNow.UtcDateTime,
            SigningCredentials = new(new SymmetricSecurityKey(_secret), SecurityAlgorithms.HmacSha256)
        });
    }
}
