// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Core.Authentication;

public sealed partial class JwtAuthentication : IRpcAuthentication
{
    private const string JwtMessagePrefix = "Bearer ";
    private const int JwtTokenTtl = 60;
    private const int JwtSecretLength = 64;

    private static readonly Task<bool> True = Task.FromResult(true);
    private static readonly Task<bool> False = Task.FromResult(false);

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SecurityKey _securityKey;
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;
    private readonly LifetimeValidator _lifetimeValidator;

    // Single entry cache: last successfully validated token
    private TokenCacheEntry? _lastToken;

    private JwtAuthentication(byte[] secret, ITimestamper timestamper, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(timestamper);

        _securityKey = new SymmetricSecurityKey(secret);
        _logger = logger;
        _timestamper = timestamper;
        _lifetimeValidator = LifetimeValidator;
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

    public Task<bool> Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            if (_logger.IsWarn) WarnTokenNotFound();
            return False;
        }

        if (!token.StartsWith(JwtMessagePrefix, StringComparison.Ordinal))
        {
            if (_logger.IsWarn) TokenMalformed();
            return False;
        }

        // fast path - reuse last successful validation for the same token
        // we keep it very cheap: one time read, one cache read, one string compare
        long nowUnixSeconds = _timestamper.UtcNow.ToUnixTimeSeconds();
        if (TryLastValidationFromCache(token, nowUnixSeconds))
        {
            return True;
        }

        return AuthenticateCore(token);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenNotFound() => _logger.Warn("Message authentication error: The token cannot be found.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TokenMalformed() => _logger.Warn($"Message authentication error: The token must start with '{JwtMessagePrefix}'.");
    }

    private async Task<bool> AuthenticateCore(string token)
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
                LifetimeValidator = _lifetimeValidator
            };

            ReadOnlyMemory<char> tokenSlice = token.AsMemory(JwtMessagePrefix.Length);
            JsonWebToken jwtToken = _handler.ReadJsonWebToken(tokenSlice);
            TokenValidationResult result = await _handler.ValidateTokenAsync(jwtToken, tokenValidationParameters);

            if (!result.IsValid)
            {
                if (_logger.IsWarn) WarnInvalidResult(result.Exception);
                return false;
            }

            DateTime now = _timestamper.UtcNow;
            long issuedAtUnix = jwtToken.IssuedAt.ToUnixTimeSeconds();
            if (Math.Abs(issuedAtUnix - now.ToUnixTimeSeconds()) <= JwtTokenTtl)
            {
                // full validation succeeded and TTL check passed - cache as last valid token
                CacheLastToken(token, issuedAtUnix);

                if (_logger.IsTrace) Trace(jwtToken, now, tokenSlice);
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

    private void CacheLastToken(string token, long issuedAtUnixSeconds)
    {
        TokenCacheEntry entry = new(token, issuedAtUnixSeconds);
        // last writer wins, atomic swap
        Interlocked.Exchange(ref _lastToken, entry);
    }

    private bool TryLastValidationFromCache(string token, long nowUnixSeconds)
    {
        // Read the last validated token entry atomically
        // this is a single entry cache because tokens tend to be reused
        // for a handful of sequential requests before a fresh token is issued
        TokenCacheEntry? entry = Volatile.Read(ref _lastToken);
        if (entry is null)
            return false;

        // Only allow cache hit if the exact same token string is being reused
        // different tokens bypass the cache and undergo full validation
        if (!string.Equals(entry.Token, token, StringComparison.Ordinal))
            return false;

        // Token reuse is only allowed within the original JWT lifetime
        // We never extend token validity beyond what the issuer intended
        // - IssuedAtUnixSeconds ensures we don't accept a token older than TTL
        if (Math.Abs(entry.IssuedAtUnixSeconds - nowUnixSeconds) > JwtTokenTtl)
        {
            // Token lifetime exceeded - drop the cached entry and force a fresh validation
            Interlocked.CompareExchange(ref _lastToken, null, entry);
            return false;
        }

        // Same token, within TTL, recently validated:
        // Accept as valid without rerunning JWT parsing and crypto checks
        return true;
    }

    [GeneratedRegex("^(0x)?[0-9a-fA-F]{64}$")]
    private static partial Regex SecretRegex();

    private record TokenCacheEntry(string Token, long IssuedAtUnixSeconds);
}
