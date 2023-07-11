// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
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
        var signingKey = new SymmetricSecurityKey(_secret);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim("iat", _clock.UtcNow.ToUnixTimeSeconds().ToString()) };
        var token = new JwtSecurityToken(claims: claims, signingCredentials: credentials);
        var handler = new JwtSecurityTokenHandler();

        return handler.WriteToken(token);
    }
}
