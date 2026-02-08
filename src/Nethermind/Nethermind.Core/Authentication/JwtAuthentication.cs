// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    // Known HS256 JWT header Base64Url encodings used by consensus clients
    // {"alg":"HS256","typ":"JWT"}
    private const string HeaderAlgTyp = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";
    // {"typ":"JWT","alg":"HS256"}
    private const string HeaderTypAlg = "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9";
    // {"alg":"HS256"} (some CLs omit typ)
    private const string HeaderAlgOnly = "eyJhbGciOiJIUzI1NiJ9";

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SecurityKey _securityKey;
    private readonly byte[] _secretBytes;
    private readonly TokenValidationParameters _tokenValidationParameters;
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;

    // Single entry cache: last successfully validated token
    private TokenCacheEntry? _lastToken;

    private JwtAuthentication(byte[] secret, ITimestamper timestamper, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(timestamper);

        _secretBytes = secret;
        _securityKey = new SymmetricSecurityKey(secret);
        _logger = logger;
        _timestamper = timestamper;
        _tokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = _securityKey,
            RequireExpirationTime = false,
            ValidateLifetime = true,
            ValidateAudience = false,
            ValidateIssuer = false,
            LifetimeValidator = LifetimeValidator
        };
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

        // fast manual HS256 validation: avoids Microsoft.IdentityModel overhead
        // returns true=accepted, false=rejected, null=not handled (fall through)
        bool? manualResult = TryValidateManual(token, nowUnixSeconds);
        if (manualResult.HasValue)
        {
            return manualResult.Value ? True : False;
        }

        // fallback to full library validation for unrecognized header formats
        return AuthenticateCore(token);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenNotFound() => _logger.Warn("Message authentication error: The token cannot be found.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TokenMalformed() => _logger.Warn($"Message authentication error: The token must start with '{JwtMessagePrefix}'.");
    }

    /// <summary>
    /// Manual HS256 JWT validation for known header formats.
    /// Returns true=accepted, false=rejected, null=not handled (fall through to library).
    /// </summary>
    [SkipLocalsInit]
    private bool? TryValidateManual(string token, long nowUnixSeconds)
    {
        // Extract raw JWT (after "Bearer ")
        ReadOnlySpan<char> jwt = token.AsSpan(JwtMessagePrefix.Length);

        // Find the two dots separating header.payload.signature
        int firstDot = jwt.IndexOf('.');
        if (firstDot < 0) return null;

        int secondDot = jwt.Slice(firstDot + 1).IndexOf('.');
        if (secondDot < 0) return null;
        secondDot += firstDot + 1;

        ReadOnlySpan<char> header = jwt[..firstDot];

        // Only handle known HS256 headers; anything else falls to AuthenticateCore
        if (!header.SequenceEqual(HeaderAlgTyp) &&
            !header.SequenceEqual(HeaderTypAlg) &&
            !header.SequenceEqual(HeaderAlgOnly))
        {
            return null;
        }

        ReadOnlySpan<char> payload = jwt[(firstDot + 1)..secondDot];
        ReadOnlySpan<char> signature = jwt[(secondDot + 1)..];

        // Compute HMAC-SHA256 over "header.payload" (ASCII bytes)
        ReadOnlySpan<char> signedPart = jwt[..secondDot];
        int signedByteCount = Encoding.ASCII.GetByteCount(signedPart);

        byte[]? rentedSignedBytes = null;
        Span<byte> signedBytes = signedByteCount <= 512
            ? stackalloc byte[512]
            : (rentedSignedBytes = ArrayPool<byte>.Shared.Rent(signedByteCount));
        signedBytes = signedBytes[..signedByteCount];

        try
        {
            Encoding.ASCII.GetBytes(signedPart, signedBytes);

            Span<byte> computedHash = stackalloc byte[32]; // SHA256 output = 32 bytes
            HMACSHA256.HashData(_secretBytes, signedBytes, computedHash);

            // Decode the provided signature from Base64Url
            int sigCharLen = signature.Length;
            // Base64Url decode: max decoded size = ceil(sigCharLen * 3 / 4)
            int maxSigBytes = (sigCharLen * 3 + 3) / 4;
            Span<byte> sigBytes = stackalloc byte[maxSigBytes];

            if (!TryBase64UrlDecode(signature, sigBytes, out int sigBytesWritten))
            {
                // Malformed signature encoding - reject
                return false;
            }

            // Compare with constant-time comparison
            if (!CryptographicOperations.FixedTimeEquals(computedHash, sigBytes[..sigBytesWritten]))
            {
                if (_logger.IsWarn) _logger.Warn("Message authentication error: Invalid token signature.");
                return false;
            }

            // Decode payload and extract "iat" (and optionally "exp") claims
            if (!TryExtractClaims(payload, out long iat, out long exp))
            {
                // No iat claim - reject (Engine API requires iat)
                return false;
            }

            // Check exp if present: token must not be expired
            if (exp > 0 && nowUnixSeconds >= exp)
            {
                if (_logger.IsWarn) _logger.Warn($"Token expired. exp: {exp}, now: {nowUnixSeconds}");
                return false;
            }

            if (Math.Abs(iat - nowUnixSeconds) > JwtTokenTtl)
            {
                if (_logger.IsWarn) _logger.Warn($"Token expired. iat: {iat}, now: {nowUnixSeconds}");
                return false;
            }

            // Signature valid, iat within TTL
            CacheLastToken(token, iat);
            return true;
        }
        finally
        {
            if (rentedSignedBytes is not null)
                ArrayPool<byte>.Shared.Return(rentedSignedBytes);
        }
    }

    [SkipLocalsInit]
    private static bool TryBase64UrlDecode(ReadOnlySpan<char> base64Url, Span<byte> output, out int bytesWritten)
    {
        // Base64Url -> Base64: replace '-' with '+', '_' with '/', add padding
        int len = base64Url.Length;
        int paddedLen = len + (4 - len % 4) % 4;

        byte[]? rented = null;
        Span<byte> utf8 = paddedLen <= 256
            ? stackalloc byte[256]
            : (rented = ArrayPool<byte>.Shared.Rent(paddedLen));
        utf8 = utf8[..paddedLen];

        try
        {
            for (int i = 0; i < len; i++)
            {
                char c = base64Url[i];
                utf8[i] = c switch
                {
                    '-' => (byte)'+',
                    '_' => (byte)'/',
                    _ => (byte)c
                };
            }

            // Add padding '='
            for (int i = len; i < paddedLen; i++)
            {
                utf8[i] = (byte)'=';
            }

            return Base64.DecodeFromUtf8(utf8, output, out _, out bytesWritten) == OperationStatus.Done;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [SkipLocalsInit]
    private static bool TryExtractClaims(ReadOnlySpan<char> payloadBase64Url, out long iat, out long exp)
    {
        iat = 0;
        exp = 0;

        // Decode payload from Base64Url
        int payloadLen = payloadBase64Url.Length;
        int maxDecodedLen = (payloadLen * 3 + 3) / 4;

        byte[]? rented = null;
        Span<byte> decoded = maxDecodedLen <= 256
            ? stackalloc byte[256]
            : (rented = ArrayPool<byte>.Shared.Rent(maxDecodedLen));

        try
        {
            if (!TryBase64UrlDecode(payloadBase64Url, decoded, out int bytesWritten))
                return false;

            // Parse JSON to find "iat" and optionally "exp"
            Utf8JsonReader reader = new(decoded[..bytesWritten]);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            bool foundIat = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if (reader.ValueTextEquals("iat"u8))
                {
                    if (!reader.Read()) return false;
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out iat))
                    {
                        foundIat = true;
                        continue;
                    }
                    return false;
                }

                if (reader.ValueTextEquals("exp"u8))
                {
                    if (!reader.Read()) return false;
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out exp))
                    {
                        continue;
                    }
                    return false;
                }

                // Skip unknown value
                reader.Read();
            }

            return foundIat;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<bool> AuthenticateCore(string token)
    {
        try
        {
            ReadOnlyMemory<char> tokenSlice = token.AsMemory(JwtMessagePrefix.Length);
            JsonWebToken jwtToken = _handler.ReadJsonWebToken(tokenSlice);
            TokenValidationResult result = await _handler.ValidateTokenAsync(jwtToken, _tokenValidationParameters);

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
