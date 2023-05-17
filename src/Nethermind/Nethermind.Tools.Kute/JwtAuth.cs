// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Nethermind.Tools.Kute;

class JwtAuth : IAuth
{
    private readonly byte[] _secret;
    private readonly Lazy<string> _token;
    private readonly ISystemClock _clock;

    public string AuthToken
    {
        get => _token.Value;
    }

    public JwtAuth(ISystemClock clock, string hexSecret)
    {
        _clock = clock;

        // TODO: Check if `hexString` is an actual Hex string.
        _secret = Enumerable.Range(0, hexSecret.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hexSecret.Substring(x, 2), 16))
            .ToArray();
        _token = new Lazy<string>(GenerateAuthToken);
    }

    private string GenerateAuthToken()
    {
        var signingKey = new SymmetricSecurityKey(_secret);
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim("iat", _clock.UtcNow.ToUnixTimeSeconds().ToString()), };
        var token = new JwtSecurityToken(claims: claims, signingCredentials: credentials);
        var handler = new JwtSecurityTokenHandler();

        return handler.WriteToken(token);
    }
}
