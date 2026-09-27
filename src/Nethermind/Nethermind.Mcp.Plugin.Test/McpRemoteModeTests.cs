// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Client;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

/// <summary>A self-signed certificate and its key written as PEM files to a temporary directory.</summary>
internal sealed class McpTestCertificate : IDisposable
{
    public const string DnsName = "mcp.test";

    private McpTestCertificate(string directory, string certificatePath, string keyPath, string thumbprint)
    {
        Directory = directory;
        CertificatePath = certificatePath;
        KeyPath = keyPath;
        Thumbprint = thumbprint;
    }

    public string Directory { get; }

    public string CertificatePath { get; }

    public string KeyPath { get; }

    public string Thumbprint { get; }

    /// <summary>Creates a P-256 certificate for <see cref="DnsName"/>, <c>localhost</c> and <c>127.0.0.1</c>.</summary>
    /// <param name="notBefore">Start of validity; defaults to one day ago.</param>
    /// <param name="notAfter">End of validity; defaults to one year from now.</param>
    /// <param name="mismatchedKey">Writes the key of another certificate instead of the matching one.</param>
    public static McpTestCertificate Create(DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, bool mismatchedKey = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mcp-tls-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={DnsName}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName(DnsName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1));

        string certificatePath = Path.Combine(directory, "cert.pem");
        string keyPath = Path.Combine(directory, "key.pem");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());

        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, (mismatchedKey ? otherKey : key).ExportPkcs8PrivateKeyPem());

        return new McpTestCertificate(directory, certificatePath, keyPath, certificate.Thumbprint);
    }

    /// <summary>Creates an HTTP client that trusts only this certificate.</summary>
    public HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && string.Equals(presented.GetCertHashString(), Thumbprint, StringComparison.OrdinalIgnoreCase),
        },
    });

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the temp directory is reclaimed by the OS anyway.
        }
    }
}

[Parallelizable(ParallelScope.All)]
public class McpRemoteModeTests
{
    private const string Token = McpTestNode.TestToken;

    [Test]
    public async Task Https_on_loopback_serves_mcp_and_rejects_plain_http()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await McpTestNode.Create(c =>
        {
            c.TlsCertificatePath = certificate.CertificatePath;
            c.TlsCertificateKeyPath = certificate.KeyPath;
        });

        Assert.That(node.Endpoint.Scheme, Is.EqualTo(Uri.UriSchemeHttps));

        using HttpClient https = certificate.CreateHttpClient();
        using HttpResponseMessage response = await https.SendAsync(McpHttp.Post(node.Endpoint, McpHttp.InitializeBody()));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await AssertPlainHttpRefused(new UriBuilder(node.Endpoint) { Scheme = Uri.UriSchemeHttp }.Uri);
    }

    [Test]
    public async Task Https_client_without_trust_is_refused()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await McpTestNode.Create(c =>
        {
            c.TlsCertificatePath = certificate.CertificatePath;
            c.TlsCertificateKeyPath = certificate.KeyPath;
        });

        using HttpClient untrusting = new();
        Assert.CatchAsync<HttpRequestException>(() => untrusting.SendAsync(McpHttp.Post(node.Endpoint, McpHttp.InitializeBody())),
            "a self-signed certificate must fail default validation, proving TLS is really negotiated");
    }

    [Test]
    public async Task Remote_mode_roundtrip_over_https_with_token()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await CreateRemoteNode(certificate);
        Uri endpoint = LoopbackEndpoint(node);

        using HttpClient https = certificate.CreateHttpClient();
        using HttpResponseMessage response = await https.SendAsync(
            McpHttp.Post(endpoint, McpHttp.InitializeBody(), host: $"{McpTestCertificate.DnsName}:{endpoint.Port}", bearer: Token));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        JsonElement result = (await McpHttp.ReadJsonRpc(response)).GetProperty("result");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("serverInfo").GetProperty("name").GetString(), Is.EqualTo("Nethermind"));
            Assert.That(result.GetProperty("instructions").GetString(), Does.Contain("node_status"));
        }
    }

    [Test]
    public async Task Remote_mode_sdk_client_lists_tools_over_https()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await CreateRemoteNode(certificate, allowedHosts: ["127.0.0.1"]);

        using HttpClient https = certificate.CreateHttpClient();
        HttpClientTransportOptions options = new()
        {
            Endpoint = LoopbackEndpoint(node),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" },
        };
        await using McpClient client = await McpClient.CreateAsync(new HttpClientTransport(options, https, ownsHttpClient: false));

        Assert.That(await client.ListToolsAsync(), Is.Not.Empty);
    }

    private static IEnumerable<TestCaseData> RemoteHostHeaderCases()
    {
        yield return new TestCaseData($"{McpTestCertificate.DnsName}:{{port}}", HttpStatusCode.OK).SetName("Allowed_host_with_port_is_accepted");
        yield return new TestCaseData("MCP.TEST:{port}", HttpStatusCode.OK).SetName("Allowed_host_is_case_insensitive");
        yield return new TestCaseData("127.0.0.1:{port}", HttpStatusCode.BadRequest).SetName("Loopback_literal_is_rejected_in_remote_mode");
        yield return new TestCaseData("localhost:{port}", HttpStatusCode.BadRequest).SetName("Localhost_is_rejected_in_remote_mode");
        yield return new TestCaseData("evil.example:{port}", HttpStatusCode.BadRequest).SetName("Foreign_host_is_rejected_in_remote_mode");
        yield return new TestCaseData("mcp.test.evil.example:{port}", HttpStatusCode.BadRequest).SetName("Suffixed_host_is_rejected_in_remote_mode");
    }

    [TestCaseSource(nameof(RemoteHostHeaderCases))]
    public async Task Remote_mode_host_header_check(string host, HttpStatusCode expected)
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await CreateRemoteNode(certificate);
        Uri endpoint = LoopbackEndpoint(node);

        using HttpClient https = certificate.CreateHttpClient();
        using HttpResponseMessage response = await https.SendAsync(
            McpHttp.Post(endpoint, McpHttp.InitializeBody(), host: host.Replace("{port}", endpoint.Port.ToString()), bearer: Token));

        Assert.That(response.StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Remote_mode_requires_the_token([Values(null, "wrong-token-wrong-token-wrong-token")] string? bearer)
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await CreateRemoteNode(certificate);
        Uri endpoint = LoopbackEndpoint(node);

        using HttpClient https = certificate.CreateHttpClient();
        using HttpResponseMessage response = await https.SendAsync(
            McpHttp.Post(endpoint, McpHttp.InitializeBody(), host: $"{McpTestCertificate.DnsName}:{endpoint.Port}", bearer: bearer));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(response.Headers.WwwAuthenticate.Select(static h => h.Scheme), Is.EqualTo(new[] { "Bearer" }));
            Assert.That(response.Headers.ConnectionClose, Is.True);
        }
    }

    [Test]
    public async Task Remote_mode_throttles_repeated_auth_failures()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await CreateRemoteNode(certificate);
        Uri endpoint = LoopbackEndpoint(node);
        string host = $"{McpTestCertificate.DnsName}:{endpoint.Port}";
        using HttpClient https = certificate.CreateHttpClient();

        for (int i = 0; i < McpAuthFailureLimiter.MaxFailures; i++)
        {
            using HttpResponseMessage failed = await https.SendAsync(McpHttp.Post(endpoint, McpHttp.InitializeBody(), host: host, bearer: $"wrong-{i}"));
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }

        using HttpResponseMessage throttled = await https.SendAsync(McpHttp.Post(endpoint, McpHttp.InitializeBody(), host: host, bearer: "wrong-again"));
        using HttpResponseMessage authorized = await https.SendAsync(McpHttp.Post(endpoint, McpHttp.InitializeBody(), host: host, bearer: Token));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(throttled.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(throttled.Headers.RetryAfter, Is.Not.Null);
            Assert.That(throttled.Headers.ConnectionClose, Is.True, "rejected clients must not keep the connection");
            Assert.That(authorized.IsSuccessStatusCode, Is.True, "the right token is accepted even from a throttled address");
        }
    }

    [Test]
    public async Task Remote_mode_never_serves_plain_http()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        await using McpTestNode node = await CreateRemoteNode(certificate);
        Uri endpoint = LoopbackEndpoint(node);

        Assert.That(node.Endpoint.Scheme, Is.EqualTo(Uri.UriSchemeHttps));
        await AssertPlainHttpRefused(new UriBuilder(endpoint) { Scheme = Uri.UriSchemeHttp }.Uri, $"{McpTestCertificate.DnsName}:{endpoint.Port}");
    }

    [Test]
    public async Task Remote_mode_without_tls_fails_to_start()
    {
        await using McpTestNode node = await McpTestNode.Create(c =>
        {
            c.Host = "0.0.0.0";
            c.AllowedHosts = [McpTestCertificate.DnsName];
        }, withAuth: true, start: false);

        Nethermind.Core.Exceptions.InvalidConfigurationException exception =
            Assert.ThrowsAsync<Nethermind.Core.Exceptions.InvalidConfigurationException>(() => node.Host.StartAsync(CancellationToken.None))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain("TlsCertificatePath"));
            Assert.That(node.Host.Endpoint, Is.Null);
        }
    }

    [Test]
    public void Limiter_blocks_after_max_failures_and_recovers_after_the_window()
    {
        ManualTimeProvider time = new();
        McpAuthFailureLimiter limiter = new(time);
        IPAddress client = IPAddress.Parse("203.0.113.7");

        for (int i = 0; i < McpAuthFailureLimiter.MaxFailures - 1; i++) limiter.RecordFailure(client);
        Assert.That(limiter.IsBlocked(client), Is.False, "one below the limit");

        limiter.RecordFailure(client);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.IsBlocked(client), Is.True);
            Assert.That(limiter.IsBlocked(IPAddress.Parse("203.0.113.8")), Is.False, "other clients are unaffected");
            Assert.That(limiter.IsBlocked(IPAddress.Parse("::ffff:203.0.113.7")), Is.True, "IPv4-mapped IPv6 counts as the IPv4 client");
        }

        time.Advance(limiter.Window);
        Assert.That(limiter.IsBlocked(client), Is.False, "failures leave the sliding window");
    }

    [Test]
    public void Limiter_groups_ipv6_clients_by_64_prefix()
    {
        McpAuthFailureLimiter limiter = new(new ManualTimeProvider());
        for (int i = 0; i < McpAuthFailureLimiter.MaxFailures; i++) limiter.RecordFailure(IPAddress.Parse($"2001:db8:1:2::{i + 1:x}"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.IsBlocked(IPAddress.Parse("2001:db8:1:2:ffff::1")), Is.True, "same /64");
            Assert.That(limiter.IsBlocked(IPAddress.Parse("2001:db8:1:3::1")), Is.False, "another /64");
        }
    }

    [Test]
    public void Limiter_is_bounded()
    {
        ManualTimeProvider time = new();
        McpAuthFailureLimiter limiter = new(time);
        for (int i = 0; i < McpAuthFailureLimiter.MaxTrackedClients + 100; i++) limiter.RecordFailure(new IPAddress(0x0A000000u + (uint)i));

        Assert.That(limiter.TrackedClients, Is.LessThanOrEqualTo(McpAuthFailureLimiter.MaxTrackedClients + 1), "overflowing clients share one bucket");

        time.Advance(limiter.Window);
        limiter.RecordFailure(IPAddress.Parse("198.51.100.1"));
        Assert.That(limiter.TrackedClients, Is.EqualTo(1), "idle entries are purged when the table is full");
    }

    [Test]
    public void Throttled_address_still_accepts_the_valid_token()
    {
        McpAuthFailureLimiter limiter = new(new ManualTimeProvider());
        McpSecurityMiddleware security = new([], Token, McpHostPolicy.Loopback(McpHostPolicy.DefaultHttpPort, []), limiter);
        IPAddress client = IPAddress.Parse("203.0.113.9");

        for (int i = 0; i < McpAuthFailureLimiter.MaxFailures; i++)
            Assert.That(security.Evaluate("127.0.0.1:8555", 8555, StringValues.Empty, "Bearer nope", client), Is.EqualTo(McpRequestVerdict.Unauthorized));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(security.Evaluate("127.0.0.1:8555", 8555, StringValues.Empty, "Bearer nope", client), Is.EqualTo(McpRequestVerdict.TooManyAttempts));
            Assert.That(security.Evaluate("127.0.0.1:8555", 8555, StringValues.Empty, $"Bearer {Token}", client), Is.EqualTo(McpRequestVerdict.Allowed));
            Assert.That(security.Evaluate("127.0.0.1:8555", 8555, StringValues.Empty, $"Bearer {Token}", IPAddress.Parse("203.0.113.10")), Is.EqualTo(McpRequestVerdict.Allowed));
            Assert.That(security.Evaluate("evil.com", 8555, StringValues.Empty, $"Bearer {Token}", client), Is.EqualTo(McpRequestVerdict.BadHost), "host is checked first");
        }
    }

    [Test]
    public void Overflow_bucket_never_blocks_new_clients()
    {
        McpAuthFailureLimiter limiter = new(new ManualTimeProvider());
        McpSecurityMiddleware security = new([], Token, McpHostPolicy.Loopback(McpHostPolicy.DefaultHttpPort, []), limiter);
        for (int i = 0; i < McpAuthFailureLimiter.MaxTrackedClients + McpAuthFailureLimiter.MaxFailures; i++)
            limiter.RecordFailure(new IPAddress(0x0A000000u + (uint)i));

        IPAddress newcomer = IPAddress.Parse("198.51.100.7");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(limiter.IsBlocked(newcomer), Is.False, "the shared overflow bucket only records");
            Assert.That(security.Evaluate("127.0.0.1:8555", 8555, StringValues.Empty, "Bearer nope", newcomer), Is.EqualTo(McpRequestVerdict.Unauthorized));
            Assert.That(security.Evaluate("127.0.0.1:8555", 8555, StringValues.Empty, $"Bearer {Token}", newcomer), Is.EqualTo(McpRequestVerdict.Allowed));
        }
    }

    /// <summary>Starts a node in remote mode on 0.0.0.0 and an ephemeral port, with TLS, the test token and <paramref name="allowedHosts"/>.</summary>
    private static Task<McpTestNode> CreateRemoteNode(McpTestCertificate certificate, string[]? allowedHosts = null) =>
        McpTestNode.Create(c =>
        {
            c.Host = "0.0.0.0";
            c.TlsCertificatePath = certificate.CertificatePath;
            c.TlsCertificateKeyPath = certificate.KeyPath;
            c.AllowedHosts = allowedHosts ?? [McpTestCertificate.DnsName];
        }, withAuth: true);

    /// <summary>The wildcard-bound endpoint, reached through the loopback interface.</summary>
    private static Uri LoopbackEndpoint(McpTestNode node) => new UriBuilder(node.Endpoint) { Host = "127.0.0.1" }.Uri;

    private static async Task AssertPlainHttpRefused(Uri plainEndpoint, string? host = null)
    {
        using HttpClient http = new();
        try
        {
            using HttpRequestMessage request = McpHttp.Post(plainEndpoint, McpHttp.InitializeBody(), host: host, bearer: Token);
            using HttpResponseMessage response = await http.SendAsync(request);
            Assert.That(response.IsSuccessStatusCode, Is.False, "plain HTTP must not be served on a TLS listener");
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Not.Contain("serverInfo"));
        }
        catch (HttpRequestException)
        {
            // Kestrel dropping the connection is also a refusal.
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
