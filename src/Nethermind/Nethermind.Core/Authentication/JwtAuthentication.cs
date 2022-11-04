//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Core.Authentication;

public class JwtAuthentication : IRpcAuthentication
{
    private readonly SecurityKey _securityKey;
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;
    private const string JwtMessagePrefix = "Bearer ";
    private const int JwtTokenTtl = 60;
    private const int JwtSecretLength = 64;

    private JwtAuthentication(byte[] secret, ITimestamper timestamper, ILogger logger)
    {
        _securityKey = new SymmetricSecurityKey(secret);
        _logger = logger;
        _timestamper = timestamper;
    }

    public static JwtAuthentication CreateFromHexSecret(string hexSecret, ITimestamper timestamper, ILogger logger)
    {
        byte[] decodedSecret = DecodeSecret(hexSecret);
        return new JwtAuthentication(decodedSecret, timestamper, logger);
    }

    public static JwtAuthentication CreateFromFileOrGenerate(string filePath, ITimestamper timestamper, ILogger logger)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists || fileInfo.Length == 0) // Generate secret
        {
            if (logger.IsInfo) logger.Info("Generating authentication secret...");

            byte[] secret = RandomNumberGenerator.GetBytes(JwtSecretLength / 2);

            try
            {
                Directory.CreateDirectory(fileInfo.DirectoryName!);
                using StreamWriter writer = new(filePath);
                writer.Write(secret.ToHexString());
            }
            catch (SystemException ex)
            {
                if (logger.IsError)
                {
                    logger.Error($"Cannot write authentication secret to '{fileInfo.FullName}'. To change file location, set the 'JsonRpc.JwtSecretFile' parameter.", ex);
                }

                throw;
            }

            if (logger.IsInfo) logger.Info($"Authentication secret has been written to '{fileInfo.FullName}'.");
            return new JwtAuthentication(secret, timestamper, logger);

        }
        else
        {
            // Secret exists read from file
            if (logger.IsInfo) logger.Info($"Reading authentication secret from '{fileInfo.FullName}'");
            string hexSecret;
            try
            {
                using StreamReader stream = new(filePath);
                hexSecret = stream.ReadToEnd();
            }
            catch (SystemException ex)
            {
                if (logger.IsError)
                {
                    logger.Error($"Cannot read authentication secret from '{fileInfo.FullName}'. To change file location, set the 'JsonRpc.JwtSecretFile' parameter.", ex);
                }

                throw;
            }

            hexSecret = hexSecret.Trim();
            if (!Regex.IsMatch(hexSecret, @"^(0x)?[0-9a-fA-F]{64}$"))
            {
                if (logger.IsError)
                {
                    logger.Error($"The specified authentication secret is not a 64-digit hex number. Delete the '{fileInfo.FullName}' to generate a new secret or set the 'JsonRpc.JwtSecretFile' parameter.");
                }

                throw new FormatException("The specified authentication secret must be a 64-digit hex number.");
            }

            return CreateFromHexSecret(hexSecret, timestamper, logger);
        }
    }

    public bool Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: The token cannot be found.");
            return false;
        }

        if (!token.StartsWith(JwtMessagePrefix, StringComparison.Ordinal))
        {
            if (_logger.IsWarn) _logger.Warn($"Message authentication error: The token must start with '{JwtMessagePrefix}'.");
            return false;
        }

        token = token.Remove(0, JwtMessagePrefix.Length);
        TokenValidationParameters tokenValidationParameters = new()
        {
            IssuerSigningKey = _securityKey,
            RequireExpirationTime = false,
            ValidateLifetime = true,
            ValidateAudience = false,
            ValidateIssuer = false,
            LifetimeValidator = LifetimeValidator
        };

        try
        {
            JwtSecurityTokenHandler handler = new();
            handler.ValidateToken(token, tokenValidationParameters, out SecurityToken _);
            JwtSecurityToken jwtToken = handler.ReadJwtToken(token);
            long iat = ((DateTimeOffset)jwtToken.IssuedAt).ToUnixTimeSeconds();
            DateTimeOffset now = _timestamper.UtcNowOffset;
            if (Math.Abs(iat - now.ToUnixTimeSeconds()) <= JwtTokenTtl)
            {
                if (_logger.IsTrace) _logger.Trace($"Message authenticated. Token: {token}, iat: {jwtToken.IssuedAt}, time: {now}");
                return true;
            }

            if (_logger.IsWarn) _logger.Warn($"Token expired. Now is {now}, token issued at {jwtToken.IssuedAt}");
            return false;
        }
        catch (SecurityTokenDecryptionFailedException)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: The token cannot be decrypted.");
            return false;
        }
        catch (SecurityTokenReplayDetectedException)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: The token has been used multiple times.");
            return false;
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            if (_logger.IsWarn) _logger.Warn("Message authentication error: Invalid token signature.");
            return false;
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Message authentication error: {ex.Message}");
            return false;
        }
    }

    private bool LifetimeValidator(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        if (expires == null) return true;
        long exp = ((DateTimeOffset)expires).ToUnixTimeSeconds();
        return _timestamper.UnixTime.SecondsLong < exp;
    }

    private static byte[] DecodeSecret(string hexSecret)
    {
        int start = hexSecret.StartsWith("0x", StringComparison.Ordinal) ? 2 : 0;
        return Enumerable.Range(start, hexSecret.Length - start)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hexSecret.Substring(x, 2), 16))
            .ToArray();
    }
}
