// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

public partial class JwtAuthentication : IRpcAuthentication
{
    private readonly SecurityKey _securityKey;
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;
    private const string JwtMessagePrefix = "Bearer ";
    private const int JwtTokenTtl = 60;
    private const int JwtSecretLength = 64;

    private JwtAuthentication(byte[] secret, ITimestamper timestamper, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secret);

        _securityKey = new SymmetricSecurityKey(secret);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timestamper = timestamper ?? throw new ArgumentNullException(nameof(timestamper));
    }

    public static JwtAuthentication FromSecret(string secret, ITimestamper timestamper, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return new(Bytes.FromHexString(secret), timestamper, logger);
    }

    public static JwtAuthentication FromFile(string filePath, ITimestamper timestamper, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(filePath);

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

            if (logger.IsWarn) logger.Warn($"The authentication secret hasn't been found in '{fileInfo.FullName}'so it has been automatically created.");

            return new(secret, timestamper, logger);
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
            if (!SecretRegex().IsMatch(hexSecret))
            {
                if (logger.IsError)
                {
                    logger.Error($"The specified authentication secret is not a 64-digit hex number. Delete the '{fileInfo.FullName}' to generate a new secret or set the 'JsonRpc.JwtSecretFile' parameter.");
                }
                throw new FormatException("The specified authentication secret must be a 64-digit hex number.");
            }

            return FromSecret(hexSecret, timestamper, logger);
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
        if (!expires.HasValue) return true;
        long exp = ((DateTimeOffset)expires).ToUnixTimeSeconds();
        return _timestamper.UnixTime.SecondsLong < exp;
    }

    [GeneratedRegex("^(0x)?[0-9a-fA-F]{64}$")]
    private static partial Regex SecretRegex();
}
