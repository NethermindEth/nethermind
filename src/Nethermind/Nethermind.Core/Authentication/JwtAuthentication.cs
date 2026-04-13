// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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

    // Manual HS256 validation limits
    private const int SHA256HashBytes = 32;
    private const int HS256SignatureSegmentLength = 44; // 43 Base64Url chars + dot separator
    private const int StackBufferSize = 256;
    private const int MaxManualJwtLength = StackBufferSize + HS256SignatureSegmentLength; // 300

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
    private readonly byte[] _secretBytes;
    private readonly TokenValidationParameters _tokenValidationParameters;
    private readonly ILogger _logger;
    private readonly ITimestamper _timestamper;

    // Single entry cache: last successfully validated token (allocation-free)
    // Write order: iat first, then token with Volatile.Write (release fence)
    // Read order: token with Volatile.Read (acquire fence), then iat
    private string? _cachedToken;
    private long _cachedTokenIat;

    private JwtAuthentication(byte[] secret, ITimestamper timestamper, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(timestamper);

        _secretBytes = secret;
        SecurityKey securityKey = new SymmetricSecurityKey(secret);
        _logger = logger;
        _timestamper = timestamper;
        _tokenValidationParameters = new TokenValidationParameters
        {
            IssuerSigningKey = securityKey,
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
        if (TryValidateManual(token, nowUnixSeconds, out bool accepted))
        {
            return accepted ? True : False;
        }

        // fallback to full library validation for unrecognized header formats
        return AuthenticateCore(token, nowUnixSeconds);

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenNotFound() => _logger.Warn("Message authentication error: The token cannot be found.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TokenMalformed() => _logger.Warn($"Message authentication error: The token must start with '{JwtMessagePrefix}'.");
    }

    /// <summary>
    /// Manual HS256 JWT validation for known header formats.
    /// Returns true if handled (result in <paramref name="accepted"/>), false to fall through to library.
    /// </summary>
    [SkipLocalsInit]
    private bool TryValidateManual(string token, long nowUnixSeconds, out bool accepted)
    {
        accepted = false;
        // Extract raw JWT (after "Bearer ")
        ReadOnlySpan<char> jwt = token.AsSpan(JwtMessagePrefix.Length);

        // Bail early: signed part must fit in StackBufferSize, plus HS256SignatureSegmentLength for the signature.
        if (jwt.Length > MaxManualJwtLength)
            return false;

        // Known HS256 header lengths: 36 (AlgTyp, TypAlg) or 20 (AlgOnly).
        // Check dot at known position and verify header in one shot — avoids IndexOf scan.
        int firstDot;
        if (jwt.Length > 36 && jwt[36] == '.' &&
            (jwt[..36].SequenceEqual(HeaderAlgTyp) || jwt[..36].SequenceEqual(HeaderTypAlg)))
        {
            firstDot = 36;
        }
        else if (jwt.Length > 20 && jwt[20] == '.' && jwt[..20].SequenceEqual(HeaderAlgOnly))
        {
            firstDot = 20;
        }
        else
        {
            return false;
        }

        // HS256 sig = 43 Base64Url chars, so second dot is at jwt.Length - HS256SignatureSegmentLength.
        // Computed directly — eliminates IndexOf scan over payload+signature.
        int secondDot = jwt.Length - HS256SignatureSegmentLength;
        if (secondDot <= firstDot || jwt[secondDot] != '.')
            return false;

        ReadOnlySpan<char> payload = jwt[(firstDot + 1)..secondDot];
        ReadOnlySpan<char> signature = jwt[(secondDot + 1)..];

        // Compute HMAC-SHA256 over "header.payload" (ASCII bytes).
        // secondDot == char count == byte count (JWT is pure ASCII).
        // Early length check guarantees secondDot <= 256.
        ReadOnlySpan<char> signedPart = jwt[..secondDot];
        Span<byte> signedBytes = stackalloc byte[StackBufferSize];
        signedBytes = signedBytes[..secondDot];

        if (Ascii.FromUtf16(signedPart, signedBytes, out _) != OperationStatus.Done)
            return false;

        Span<byte> computedHash = stackalloc byte[SHA256HashBytes];
        HMACSHA256.HashData(_secretBytes, signedBytes, computedHash);

        Span<byte> sigBytes = stackalloc byte[SHA256HashBytes];
        if (Base64Url.DecodeFromChars(signature, sigBytes, out _, out int sigBytesWritten) != OperationStatus.Done
            || sigBytesWritten != SHA256HashBytes)
        {
            return true;
        }

        if (!CryptographicOperations.FixedTimeEquals(computedHash, sigBytes))
        {
            if (_logger.IsWarn) WarnInvalidSig();
            return true;
        }

        if (!TryExtractClaims(payload, out long iat, out long exp))
            return false; // sig valid but can't parse claims — let library handle it

        if (exp > 0 && nowUnixSeconds >= exp)
        {
            if (_logger.IsWarn) WarnTokenExpiredExp(exp, nowUnixSeconds);
            return true;
        }

        // Overflow-safe absolute-difference check: casting to ulong maps negative values to
        // large positives, so (ulong)(a - b + c) > (ulong)(2*c) is equivalent to |a - b| > c
        // without needing Math.Abs (which can overflow on long.MinValue).
        if ((ulong)(iat - nowUnixSeconds + JwtTokenTtl) > (ulong)(JwtTokenTtl * 2))
        {
            if (_logger.IsWarn) WarnTokenExpiredIat(iat, nowUnixSeconds);
            return true;
        }

        CacheLastToken(token, iat);
        accepted = true;
        return true;


        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenExpiredExp(long e, long now) => _logger.Warn($"Token expired. exp: {e}, now: {now}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenExpiredIat(long i, long now) => _logger.Warn($"Token expired. iat: {i}, now: {now}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnInvalidSig() => _logger.Warn("Message authentication error: Invalid token signature.");
    }

    /// <summary>
    /// Extract "iat" (required) and "exp" (optional) integer claims from a JWT payload.
    /// Uses a lightweight byte scanner instead of Utf8JsonReader to avoid:
    /// - 552-byte stack frame (Utf8JsonReader struct ~200 bytes + locals)
    /// - 432-byte prolog zeroing loop
    /// - ArrayPool rent/return + try/finally
    /// - 90 inlined reader methods bloating to 2979 bytes of native code
    /// </summary>
    [SkipLocalsInit]
    private static bool TryExtractClaims(ReadOnlySpan<char> payloadBase64Url, out long iat, out long exp)
    {
        iat = 0;
        exp = 0;

        // Decode payload from Base64Url into a fixed stack buffer.
        // Engine API payloads are tiny (~30 bytes). If decoded output exceeds
        // StackBufferSize bytes, DecodeFromChars returns DestinationTooSmall → we reject.
        Span<byte> decoded = stackalloc byte[StackBufferSize];
        if (Base64Url.DecodeFromChars(payloadBase64Url, decoded, out _, out int bytesWritten) != OperationStatus.Done)
            return false;

        // Scan decoded UTF-8 bytes for "iat" and "exp" keys with integer values.
        // JWT payloads are compact JSON objects: {"iat":NNNNN,"exp":NNNNN}
        // The scanner finds quoted 3-letter keys and parses the integer after the colon.
        ReadOnlySpan<byte> json = decoded[..bytesWritten];
        bool foundIat = false;

        for (int i = 0; i < json.Length - 4; i++)
        {
            if (json[i] != '"') continue;

            byte k1 = json[i + 1], k2 = json[i + 2], k3 = json[i + 3];
            if (json[i + 4] != '"') continue;

            if (k1 == 'i' && k2 == 'a' && k3 == 't')
            {
                foundIat = TryParseClaimValue(json, i + 5, out iat);
            }
            else if (k1 == 'e' && k2 == 'x' && k3 == 'p')
            {
                TryParseClaimValue(json, i + 5, out exp);
            }
        }

        return foundIat;
    }

    /// <summary>
    /// Parse an integer claim value starting at <paramref name="pos"/> (expects ":digits").
    /// The <c>(uint)pos &lt; (uint)json.Length</c> pattern collapses a two-condition bounds check
    /// (<c>pos &gt;= 0 &amp;&amp; pos &lt; Length</c>) into a single unsigned comparison, allowing the
    /// JIT to eliminate the redundant range check on the subsequent indexer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseClaimValue(ReadOnlySpan<byte> json, int pos, out long value)
    {
        value = 0;
        // Skip optional whitespace before colon
        while ((uint)pos < (uint)json.Length && json[pos] == ' ') pos++;
        if ((uint)pos >= (uint)json.Length || json[pos] != ':') return false;
        pos++;
        // Skip optional whitespace after colon
        while ((uint)pos < (uint)json.Length && json[pos] == ' ') pos++;
        // Parse unsigned integer digits
        bool hasDigit = false;
        while ((uint)pos < (uint)json.Length)
        {
            uint digit = (uint)(json[pos] - '0');
            if (digit > 9) break;
            value = value * 10 + digit;
            hasDigit = true;
            pos++;
        }
        return hasDigit;
    }

    /// <summary>
    /// Library-based JWT validation for unrecognized header formats.
    /// Checks for synchronous Task completion to avoid async state machine on the hot path.
    /// </summary>
    private Task<bool> AuthenticateCore(string token, long nowUnixSeconds)
    {
        try
        {
            ReadOnlyMemory<char> tokenSlice = token.AsMemory(JwtMessagePrefix.Length);
            JsonWebToken jwtToken = _handler.ReadJsonWebToken(tokenSlice);
            Task<TokenValidationResult> task = _handler.ValidateTokenAsync(jwtToken, _tokenValidationParameters);

            // HS256 validation is CPU-bound → task is almost always already completed.
            // Avoid async state machine overhead for the common synchronous path.
            return task.IsCompletedSuccessfully
                ? ValidateLibraryResult(task.GetAwaiter().GetResult(), token, jwtToken, nowUnixSeconds) ? True : False
                : AwaitValidation(task, token, jwtToken, nowUnixSeconds);
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) WarnAuthError(ex);
            return False;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnAuthError(Exception? ex) => _logger.Warn($"Message authentication error: {ex?.Message}");
    }

    private bool ValidateLibraryResult(TokenValidationResult result, string token, JsonWebToken jwtToken, long nowUnixSeconds)
    {
        if (!result.IsValid)
        {
            if (_logger.IsWarn) WarnInvalidResult(result.Exception);
            return false;
        }

        long issuedAtUnix = jwtToken.IssuedAt.ToUnixTimeSeconds();

        // Unsigned range check: |iat - now| <= TTL without Math.Abs overflow guard
        if ((ulong)(issuedAtUnix - nowUnixSeconds + JwtTokenTtl) > (ulong)(JwtTokenTtl * 2))
        {
            if (_logger.IsWarn) WarnTokenExpired(issuedAtUnix, nowUnixSeconds);
            return false;
        }

        CacheLastToken(token, issuedAtUnix);
        if (_logger.IsTrace) TraceAuth(jwtToken, nowUnixSeconds, token);
        return true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnInvalidResult(Exception? ex)
        {
            _logger.Warn(ex switch
            {
                SecurityTokenDecryptionFailedException => "Message authentication error: The token cannot be decrypted.",
                SecurityTokenReplayDetectedException => "Message authentication error: The token has been used multiple times.",
                SecurityTokenInvalidSignatureException => "Message authentication error: Invalid token signature.",
                _ => $"Message authentication error: {ex?.Message}"
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnTokenExpired(long iat, long now)
            => _logger.Warn($"Token expired. iat: {iat}, now: {now}");

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceAuth(JsonWebToken jwt, long now, string tok)
            => _logger.Trace($"Message authenticated. Token: {tok.AsMemory(JwtMessagePrefix.Length)}, iat: {jwt.IssuedAt}, time: {now}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<bool> AwaitValidation(Task<TokenValidationResult> task, string token, JsonWebToken jwtToken, long nowUnixSeconds)
    {
        try
        {
            return ValidateLibraryResult(await task, token, jwtToken, nowUnixSeconds);
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) WarnAuthError(ex);
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void WarnAuthError(Exception? ex) => _logger.Warn($"Message authentication error: {ex?.Message}");
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CacheLastToken(string token, long issuedAtUnixSeconds)
    {
        // Write iat first (plain store), then token with release fence.
        // Reader uses acquire fence on token, then reads iat — guarantees
        // the iat visible is at least as fresh as the token that was read.
        _cachedTokenIat = issuedAtUnixSeconds;
        Volatile.Write(ref _cachedToken, token);
    }

    private bool TryLastValidationFromCache(string token, long nowUnixSeconds)
    {
        // Acquire fence on token read; guarantees _cachedTokenIat is at least as fresh
        string? cached = Volatile.Read(ref _cachedToken);
        if (cached is null)
            return false;

        if (!string.Equals(cached, token, StringComparison.Ordinal))
            return false;

        // Unsigned range check: |iat - now| <= TTL
        if ((ulong)(_cachedTokenIat - nowUnixSeconds + JwtTokenTtl) > (ulong)(JwtTokenTtl * 2))
        {
            Volatile.Write(ref _cachedToken, null);
            return false;
        }

        return true;
    }

    [GeneratedRegex("^(0x)?[0-9a-fA-F]{64}$")]
    private static partial Regex SecretRegex();

}
