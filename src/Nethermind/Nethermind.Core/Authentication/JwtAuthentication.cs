// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Core.Authentication;

public sealed partial class JwtAuthentication : IRpcAuthentication
{
    private static readonly Task<bool> False = Task.FromResult(false);
    private readonly JsonWebTokenHandler _handler = new();
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
        _logger = logger;
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

    public Task<bool> Authenticate(string? authToken)
    {
        if (string.IsNullOrEmpty(authToken))
        {
            if (_logger.IsWarn) WarnTokenNotFound();
            return False;
        }

        if (!authToken.StartsWith(JwtMessagePrefix, StringComparison.Ordinal))
        {
            if (_logger.IsWarn) TokenMalformed();
            return False;
        }

        return AuthenticateCore(authToken);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenNotFound() => _logger.Warn("Message authentication error: The token cannot be found.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TokenMalformed() => _logger.Warn($"Message authentication error: The token must start with '{JwtMessagePrefix}'.");
    }

    private async Task<bool> AuthenticateCore(string authToken)
    {
        try
        {
            TokenValidationParameters tokenValidationParameters = new()
            {
                IssuerSigningKey = _securityKey,
                RequireExpirationTime = false,
                ValidateLifetime = true,
                ValidateAudience = false,
                ValidateIssuer = false,
                LifetimeValidator = LifetimeValidator
            };

            ReadOnlyMemory<char> token = authToken.AsMemory(JwtMessagePrefix.Length);
            JsonWebToken jwtToken = _handler.ReadJsonWebToken(token);
            TokenValidationResult result = await _handler.ValidateTokenAsync(jwtToken, tokenValidationParameters);

            if (!result.IsValid)
            {
                if (_logger.IsWarn) WarnInvalidResult(result.Exception);
                return false;
            }

            DateTime now = _timestamper.UtcNow;
            if (Math.Abs(jwtToken.IssuedAt.ToUnixTimeSeconds() - now.ToUnixTimeSeconds()) <= JwtTokenTtl)
            {
                if (_logger.IsTrace) Trace(jwtToken, now, token);
                return true;
            }

            if (_logger.IsWarn) WarnTokenExpired(jwtToken, now);
            return false;
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) WarnAuthenticationError(ex);
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnInvalidResult(Exception? ex)
        {
            if (ex is SecurityTokenDecryptionFailedException)
            {
                _logger.Warn("Message authentication error: The token cannot be decrypted.");
            }
            else if (ex is SecurityTokenReplayDetectedException)
            {
                _logger.Warn("Message authentication error: The token has been used multiple times.");
            }
            else if (ex is SecurityTokenInvalidSignatureException)
            {
                _logger.Warn("Message authentication error: Invalid token signature.");
            }
            else
            {
                WarnAuthenticationError(ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnAuthenticationError(Exception? ex) => _logger.Warn($"Message authentication error: {ex?.Message}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenExpired(JsonWebToken jwtToken, DateTime now)
            => _logger.Warn($"Token expired. Now is {now}, token issued at {jwtToken.IssuedAt}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Trace(JsonWebToken jwtToken, DateTime now, ReadOnlyMemory<char> token)
            => _logger.Trace($"Message authenticated. Token: {token}, iat: {jwtToken.IssuedAt}, time: {now}");
    }

    private bool LifetimeValidator(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        if (!expires.HasValue) return true;
        return _timestamper.UnixTime.SecondsLong < expires.Value.ToUnixTimeSeconds();
    }

    [GeneratedRegex("^(0x)?[0-9a-fA-F]{64}$")]
    private static partial Regex SecretRegex();
}
