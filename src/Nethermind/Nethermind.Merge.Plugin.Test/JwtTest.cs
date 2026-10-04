// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class JwtTest
{
    private const string HexSecret = "5166546A576E5A7234753778214125442A472D4A614E645267556B5870327335";
    private const long TestIat = 1644994971;

    [Test]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzV9.HfWy49SIyB12PBB_xEpy6IAiIan5mIqD6Jzeh_J1QNw", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5Njd9.YGA0v88qMS7lp41wJQv9Msru6dwrNOHXHYiDsuhuScU", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzMsImlhdCI6MTY0NDk5NDk3MX0.ADc_b_tCac2uRHcNCekHvHV-qQ8hNyUjdxCVPETd3Os", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJiYXIiOiJiYXoiLCJpYXQiOjE2NDQ5OTQ5NzF9.UZmoAYPGvKoWvz3KcXuxkDnVIF4Fn7QT7z9RwZgSREo", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTUwMzJ9.21Oqg-Q5Ug3HfxL0JmuOV_Caer7PtgwUhKkiLAQsBCI", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5MTB9.o6H-ep8T3wI79OoCbs6QIK62BDoFMCg_jq75noyWbbI", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PMe", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF9.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PMEe", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NzF8.RmIbZajyYGF9fhAq7A9YrTetdf15ebHIJiSdAhX7PME", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.d88KZjmZ_nL0JTnsF6SR1BRBCjus4U3M-390HDDDNRc", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE2NDQ5OTQ5NzEsImlhdCI6MTY0NDk5NDk3MX0.wU4z8ROPW-HaOgrUBG0FqTEutt7rWVsWMqXLvdEl_wI", "false")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTUwMzF9.fI6IzcITJKC5HJfsrnMc2kxKmi6kZEVcEjFcRzL6UGs", "true")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpYXQiOjE2NDQ5OTQ5NjF9.huhtaE1cUU2JuhqKmeTrHC3wgl2Tp_1pVh7DuYkKrQo", "true")]
    public async Task long_key_tests(string token, bool expected)
    {
        ManualTimestamper manualTimestamper = new() { UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat).UtcDateTime };
        IRpcAuthentication authentication = JwtAuthentication.FromSecret(HexSecret, manualTimestamper, LimboTraceLogger.Instance);
        IRpcAuthentication authenticationWithPrefix = JwtAuthentication.FromSecret("0x" + HexSecret, manualTimestamper, LimboTraceLogger.Instance);
        bool actual = await authentication.Authenticate(token);
        Assert.That(actual, Is.EqualTo(expected));
        actual = await authenticationWithPrefix.Authenticate(token);
        Assert.That(expected, Is.EqualTo(actual));
    }

    // --- Guard clause tests (Authenticate entry) ---

    [Test]
    public async Task Null_token_returns_false() =>
        Assert.That(await CreateAuth().Authenticate(null!), Is.False);

    [Test]
    public async Task Empty_token_returns_false() =>
        Assert.That(await CreateAuth().Authenticate(""), Is.False);

    [Test]
    public async Task Missing_bearer_prefix_returns_false()
    {
        // Valid JWT but without "Bearer " prefix
        string jwt = CreateJwt("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", $"{{\"iat\":{TestIat}}}");
        Assert.That(await CreateAuth().Authenticate(jwt["Bearer ".Length..]), Is.False);
    }

    // --- Alternate header format tests (TryValidateManual branches) ---

    [Test]
    public async Task HeaderTypAlg_valid_token_returns_true()
    {
        // {"typ":"JWT","alg":"HS256"} — reversed field order, 36-char Base64Url header
        string token = CreateJwt("{\"typ\":\"JWT\",\"alg\":\"HS256\"}", $"{{\"iat\":{TestIat}}}");
        Assert.That(await CreateAuth().Authenticate(token), Is.True);
    }

    [Test]
    public async Task HeaderAlgOnly_valid_token_returns_true()
    {
        // {"alg":"HS256"} — no typ field, 20-char Base64Url header
        string token = CreateJwt("{\"alg\":\"HS256\"}", $"{{\"iat\":{TestIat}}}");
        Assert.That(await CreateAuth().Authenticate(token), Is.True);
    }

    [Test]
    public async Task HeaderAlgOnly_expired_iat_returns_false()
    {
        string token = CreateJwt("{\"alg\":\"HS256\"}", $"{{\"iat\":{TestIat - 200}}}");
        Assert.That(await CreateAuth().Authenticate(token), Is.False);
    }

    // --- AuthenticateCore fallback (unrecognized header → library path) ---

    [Test]
    public async Task Unrecognized_header_valid_token_falls_to_library()
    {
        // Extra "kid" field makes the Base64Url header unrecognized → AuthenticateCore path
        string token = CreateJwt("{\"alg\":\"HS256\",\"typ\":\"JWT\",\"kid\":\"1\"}", $"{{\"iat\":{TestIat}}}");
        Assert.That(await CreateAuth().Authenticate(token), Is.True);
    }

    [Test]
    public async Task Unrecognized_header_expired_iat_returns_false()
    {
        string token = CreateJwt("{\"alg\":\"HS256\",\"typ\":\"JWT\",\"kid\":\"1\"}", $"{{\"iat\":{TestIat - 200}}}");
        Assert.That(await CreateAuth().Authenticate(token), Is.False);
    }

    // --- Cache path tests (TryLastValidationFromCache) ---

    [Test]
    public async Task Cache_hit_returns_true_on_repeated_call()
    {
        IRpcAuthentication auth = CreateAuth();
        string token = CreateJwt("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", $"{{\"iat\":{TestIat}}}");

        Assert.That(await auth.Authenticate(token), Is.True);
        // Second call with same token should hit cache and still return true
        Assert.That(await auth.Authenticate(token), Is.True);
    }

    [Test]
    public async Task Cached_token_respects_iat_window(
        [Values] bool useLibraryFallback,
        [Values(null, 60, 120)] int? expirationAfterIssue,
        [Values(-61, -60, 60, 61)] int secondsAfterIssue)
    {
        ManualTimestamper ts = new() { UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat).UtcDateTime };
        IRpcAuthentication auth = JwtAuthentication.FromSecret(HexSecret, ts, LimboTraceLogger.Instance);
        string payload = expirationAfterIssue.HasValue
            ? $"{{\"iat\":{TestIat},\"exp\":{TestIat + expirationAfterIssue.Value}}}"
            : $"{{\"iat\":{TestIat}}}";
        string token = CreateJwt(CreateHeader(useLibraryFallback), payload);

        Assert.That(await auth.Authenticate(token), Is.True);

        ts.UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat + secondsAfterIssue).UtcDateTime;
        IRpcAuthentication uncached = JwtAuthentication.FromSecret(HexSecret, ts, LimboTraceLogger.Instance);
        bool expected = Math.Abs(secondsAfterIssue) <= 60
            && (!expirationAfterIssue.HasValue || secondsAfterIssue < expirationAfterIssue.Value);
        Assert.That(await uncached.Authenticate(token), Is.EqualTo(expected));
        Assert.That(await auth.Authenticate(token), Is.EqualTo(expected));
    }

    [Test]
    public async Task Cache_miss_different_token_revalidates()
    {
        IRpcAuthentication auth = CreateAuth();
        string token1 = CreateJwt("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", $"{{\"iat\":{TestIat}}}");
        string token2 = CreateJwt("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", $"{{\"iat\":{TestIat + 1}}}");

        Assert.That(await auth.Authenticate(token1), Is.True);
        // Different token — cache miss, must revalidate
        Assert.That(await auth.Authenticate(token2), Is.True);
    }

    [Test]
    public async Task Cached_token_respects_expiration(
        [Values] bool useLibraryFallback,
        [Values(2, 3)] int secondsAfterIssue)
    {
        ManualTimestamper ts = new() { UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat).UtcDateTime };
        IRpcAuthentication auth = JwtAuthentication.FromSecret(HexSecret, ts, LimboTraceLogger.Instance);
        string token = CreateJwt(CreateHeader(useLibraryFallback), $"{{\"iat\":{TestIat},\"exp\":{TestIat + 2}}}");

        Assert.That(await auth.Authenticate(token), Is.True);
        ts.UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat + 1).UtcDateTime;
        Assert.That(await auth.Authenticate(token), Is.True);

        ts.UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat + secondsAfterIssue).UtcDateTime;
        IRpcAuthentication uncached = JwtAuthentication.FromSecret(HexSecret, ts, LimboTraceLogger.Instance);
        Assert.That(await uncached.Authenticate(token), Is.False, "Uncached validation must reject an expired token");
        Assert.That(await auth.Authenticate(token), Is.False, "Cached validation must reject the same expired token");
    }

    [Test]
    public async Task Cached_token_skips_library_validation([Values(null, 2, 120)] int? expirationAfterIssue)
    {
        ManualTimestamper ts = new() { UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat).UtcDateTime };
        TestLogger logger = new();
        IRpcAuthentication auth = JwtAuthentication.FromSecret(HexSecret, ts, new ILogger(logger));
        string payload = expirationAfterIssue.HasValue
            ? $"{{\"iat\":{TestIat},\"exp\":{TestIat + expirationAfterIssue.Value}}}"
            : $"{{\"iat\":{TestIat}}}";
        string token = CreateJwt(CreateHeader(useLibraryFallback: true), payload);

        Assert.That(await auth.Authenticate(token), Is.True);
        Assert.That(logger.LogList, Has.Count.EqualTo(1));

        ts.UtcNow = DateTimeOffset.FromUnixTimeSeconds(TestIat + 1).UtcDateTime;
        Assert.That(await auth.Authenticate(token), Is.True);
        Assert.That(logger.LogList, Has.Count.EqualTo(1), "A cache hit must skip library validation and its trace log");
    }

    // --- Helpers ---

    private static string CreateHeader(bool useLibraryFallback) => useLibraryFallback
        ? "{\"alg\":\"HS256\",\"typ\":\"JWT\",\"kid\":\"1\"}"
        : "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";

    private static IRpcAuthentication CreateAuth(long nowUnixSeconds = TestIat)
    {
        ManualTimestamper ts = new() { UtcNow = DateTimeOffset.FromUnixTimeSeconds(nowUnixSeconds).UtcDateTime };
        return JwtAuthentication.FromSecret(HexSecret, ts, LimboTraceLogger.Instance);
    }

    private static string CreateJwt(string headerJson, string payloadJson)
    {
        byte[] secret = Bytes.FromHexString(HexSecret);
        string header = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        byte[] sig = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes($"{header}.{payload}"));
        return $"Bearer {header}.{payload}.{Base64UrlEncode(sig)}";
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
