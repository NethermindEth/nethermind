// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Nethermind.FastRpc;

/// <summary>
/// Minimal HS256 JWT helper for the fast RPC transport.
/// </summary>
public static class FastJwt
{
    private const string Algorithm = "HS256";

    /// <summary>
    /// Creates a compact HS256 token for benchmarks and local tests.
    /// </summary>
    public static string CreateHmacSha256Token(ReadOnlySpan<byte> secret)
    {
        string encodedHeader = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        string encodedPayload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("""{"iat":1700000000}"""));
        string signingInput = $"{encodedHeader}.{encodedPayload}";
        byte[] signature = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    /// <summary>
    /// Validates a compact HS256 bearer token signature.
    /// </summary>
    public static bool ValidateHmacSha256(string token, ReadOnlySpan<byte> secret)
    {
        int firstDot = token.IndexOf('.');
        if (firstDot <= 0) return false;

        int secondDotOffset = token.AsSpan(firstDot + 1).IndexOf('.');
        if (secondDotOffset <= 0) return false;

        int secondDot = firstDot + 1 + secondDotOffset;
        if (secondDot == token.Length - 1) return false;

        try
        {
            if (!HasExpectedAlgorithm(token[..firstDot])) return false;

            byte[] signingInput = Encoding.ASCII.GetBytes(token[..secondDot]);
            byte[] expectedSignature = HMACSHA256.HashData(secret, signingInput);
            byte[] actualSignature = WebEncoders.Base64UrlDecode(token[(secondDot + 1)..]);

            return expectedSignature.Length == actualSignature.Length
                && CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExpectedAlgorithm(string encodedHeader)
    {
        byte[] headerBytes = WebEncoders.Base64UrlDecode(encodedHeader);
        using JsonDocument header = JsonDocument.Parse(headerBytes);

        return header.RootElement.TryGetProperty("alg", out JsonElement alg)
            && string.Equals(alg.GetString(), Algorithm, StringComparison.Ordinal);
    }
}
