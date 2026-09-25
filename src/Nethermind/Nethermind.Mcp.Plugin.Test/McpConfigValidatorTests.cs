// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.IO;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Config;
using NUnit.Framework;

namespace Nethermind.Mcp.Plugin.Test;

[Parallelizable(ParallelScope.All)]
public class McpConfigValidatorTests
{
    private const int McpPort = 8555;
    private static readonly string ValidToken = new('a', 32);

    [Test]
    public void Defaults_are_disabled_and_valid()
    {
        McpConfig config = new();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Enabled, Is.False, "MCP must be opt-in");
            Assert.That(config.Host, Is.EqualTo("127.0.0.1"));
            Assert.That(config.AuthTokenFile, Is.Null);
            Assert.That(config.AllowedOrigins, Is.Empty);
            Assert.That(() => McpConfigValidator.Validate(config, EnabledRpc()), Throws.Nothing);
        }
    }

    [Test]
    public void Loopback_hosts_are_accepted([Values("127.0.0.1", "127.0.0.2", "::1")] string host) =>
        Assert.That(() => McpConfigValidator.Validate(new McpConfig { Host = host }, EnabledRpc()), Throws.Nothing);

    [Test]
    public void Host_names_and_unparseable_hosts_are_rejected([Values("localhost", "evil.com", "node.example.com", "", " ", " 127.0.0.1", "127.0.0.1:8555")] string host)
    {
        InvalidConfigurationException exception = AssertInvalid(new McpConfig { Host = host }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
        Assert.That(exception.Message, Does.Contain("IP address literal"));
    }

    [Test]
    public void Non_loopback_host_without_remote_settings_is_rejected(
        [Values("0.0.0.0", "192.168.1.10", "10.0.0.1", "::", "::ffff:192.168.1.10")] string host)
    {
        InvalidConfigurationException exception = AssertInvalid(new McpConfig { Host = host }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
        Assert.That(exception.Message, Does.Contain("remote mode").And.Contain("TlsCertificatePath"));
    }

    private static IEnumerable<TestCaseData> RemoteModeFailureCases()
    {
        yield return Case("Remote_without_tls", (c, _) => { c.TlsCertificatePath = null; c.TlsCertificateKeyPath = null; }, "Plain HTTP is never served");
        yield return Case("Remote_with_certificate_only", (c, _) => c.TlsCertificateKeyPath = null, "must be set together");
        yield return Case("Remote_with_key_only", (c, _) => c.TlsCertificatePath = null, "must be set together");
        yield return Case("Remote_without_token", (c, _) => c.AuthTokenFile = null, "requires bearer authentication");
        yield return Case("Remote_without_allowed_hosts", (c, _) => c.AllowedHosts = [], "requires Mcp.AllowedHosts");
        yield return Case("Remote_with_missing_certificate_file", (c, dir) => c.TlsCertificatePath = Path.Combine(dir, "missing.pem"), "TlsCertificatePath");
        yield return Case("Remote_with_missing_key_file", (c, dir) => c.TlsCertificateKeyPath = Path.Combine(dir, "missing.pem"), "TlsCertificateKeyPath");
        yield return Case("Remote_with_garbage_certificate", (c, dir) => c.TlsCertificatePath = Write(dir, "garbage.pem", "not a certificate"), "matching");
        yield return Case("Remote_with_key_as_certificate", (c, _) => c.TlsCertificatePath = c.TlsCertificateKeyPath, "matching");
        yield return Case("Remote_with_short_token", (c, dir) => c.AuthTokenFile = Write(dir, "short.token", "short"), "at least 32 characters");
        yield return Case("Remote_with_malformed_allowed_host", (c, _) => c.AllowedHosts = ["https://node.example.com"], "AllowedHosts entry");

        static TestCaseData Case(string name, Action<McpConfig, string> configure, string expected) =>
            new TestCaseData(configure, expected).SetName(name);

        static string Write(string dir, string name, string content)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, content);
            return path;
        }
    }

    [TestCaseSource(nameof(RemoteModeFailureCases))]
    public void Remote_mode_misconfiguration_is_rejected(Action<McpConfig, string> configure, string expectedMessage)
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        McpConfig config = RemoteConfig(certificate);
        configure(config, certificate.Directory);

        InvalidConfigurationException exception = AssertInvalid(config, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    [Test]
    public void Remote_mode_with_mismatched_key_is_rejected()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create(mismatchedKey: true);

        InvalidConfigurationException exception = AssertInvalid(RemoteConfig(certificate), EnabledRpc(), ExitCodes.ForbiddenOptionValue);
        Assert.That(exception.Message, Does.Contain("matching"));
    }

    [Test]
    public void Remote_mode_with_complete_settings_is_accepted([Values("0.0.0.0", "::", "192.168.1.10")] string host)
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        McpConfig config = RemoteConfig(certificate);
        config.Host = host;

        using McpListenerSettings settings = McpConfigValidator.Load(config, EnabledRpc());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.IsRemote, Is.True);
            Assert.That(settings.Certificate, Is.Not.Null);
            Assert.That(settings.Certificate!.HasPrivateKey, Is.True);
            Assert.That(settings.AuthToken, Is.EqualTo(ValidToken));
            Assert.That(settings.AllowedHosts, Is.EqualTo(new[] { new McpAllowedHost("node.example.com", null) }));
            Assert.That(settings.Warnings, Is.Empty);
        }
    }

    [Test]
    public void Loopback_with_tls_is_accepted_and_loads_the_certificate()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        McpConfig config = new() { TlsCertificatePath = certificate.CertificatePath, TlsCertificateKeyPath = certificate.KeyPath };

        using McpListenerSettings settings = McpConfigValidator.Load(config, EnabledRpc());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.IsRemote, Is.False);
            Assert.That(settings.Certificate, Is.Not.Null);
            Assert.That(settings.AuthToken, Is.Null, "a token stays optional on loopback");
        }
    }

    [Test]
    public void Loopback_with_half_tls_config_is_rejected()
    {
        using McpTestCertificate certificate = McpTestCertificate.Create();
        AssertInvalid(new McpConfig { TlsCertificatePath = certificate.CertificatePath }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
    }

    private static IEnumerable<TestCaseData> CertificateValidityCases()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        yield return new TestCaseData(now.AddDays(-30), now.AddDays(-1), "expired").SetName("Expired_certificate_warns");
        yield return new TestCaseData(now.AddDays(-30), now.AddDays(5), "renew it soon").SetName("Soon_expiring_certificate_warns");
        yield return new TestCaseData(now.AddDays(2), now.AddDays(300), "not valid before").SetName("Not_yet_valid_certificate_warns");
    }

    [TestCaseSource(nameof(CertificateValidityCases))]
    public void Certificate_validity_problems_warn_but_do_not_fail(DateTimeOffset notBefore, DateTimeOffset notAfter, string expectedWarning)
    {
        using McpTestCertificate certificate = McpTestCertificate.Create(notBefore, notAfter);

        using McpListenerSettings settings = McpConfigValidator.Load(RemoteConfig(certificate), EnabledRpc());
        Assert.That(settings.Warnings, Has.Some.Contains(expectedWarning));
    }

    [TestCase("node.example.com", "node.example.com", null)]
    [TestCase("Node.Example.COM:8555", "node.example.com", 8555)]
    [TestCase("203.0.113.7", "203.0.113.7", null)]
    [TestCase("203.0.113.7:443", "203.0.113.7", 443)]
    [TestCase("[2001:DB8::1]", "[2001:db8::1]", null)]
    [TestCase("[2001:db8:0::1]:8555", "[2001:db8::1]", 8555)]
    [TestCase("localhost", "localhost", null)]
    public void Well_formed_allowed_host_is_normalized(string entry, string expectedName, int? expectedPort) =>
        Assert.That(McpConfigValidator.ParseAllowedHosts([entry]), Is.EqualTo(new[] { new McpAllowedHost(expectedName, expectedPort) }));

    [Test]
    public void Malformed_allowed_host_is_rejected(
        [Values("", " ", "*", "*.example.com", "http://node.example.com", "node.example.com/mcp", "user@node.example.com",
            "node.example.com:", "node.example.com:0", "node.example.com:65536", "node.example.com:+1", "2001:db8::1", "[2001:db8::1",
            "[not-ipv6]:80", "[2001:db8::1]x", "node example.com")] string entry)
    {
        InvalidConfigurationException exception = AssertInvalid(new McpConfig { AllowedHosts = [entry] }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
        Assert.That(exception.Message, Does.Contain("AllowedHosts entry"));
    }

    [Test]
    public void Port_outside_range_is_rejected([Values(-1, 65536, int.MaxValue)] int port) =>
        AssertInvalid(new McpConfig { Port = port }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);

    [Test]
    public void Ephemeral_port_is_accepted() =>
        Assert.That(() => McpConfigValidator.Validate(new McpConfig { Port = 0 }, EnabledRpc()), Throws.Nothing);

    private static IEnumerable<TestCaseData> PortCollisionCases()
    {
        yield return new TestCaseData(new JsonRpcConfig { Enabled = true, Port = McpPort }, null).SetName("Collides_with_JsonRpc_Port");
        yield return new TestCaseData(new JsonRpcConfig { Enabled = true, WebSocketsPort = McpPort }, null).SetName("Collides_with_JsonRpc_WebSocketsPort");
        yield return new TestCaseData(new JsonRpcConfig { Enabled = true, EnginePort = McpPort }, null).SetName("Collides_with_JsonRpc_EnginePort");
        yield return new TestCaseData(new JsonRpcConfig { Enabled = false, EnginePort = McpPort }, null).SetName("Collides_with_JsonRpc_EnginePort_when_JsonRpc_disabled");
        yield return new TestCaseData(new JsonRpcConfig { Enabled = true, AdditionalRpcUrls = [$"http://127.0.0.1:{McpPort}|http|eth"] }, null).SetName("Collides_with_JsonRpc_AdditionalRpcUrls");
        yield return new TestCaseData(EnabledRpc(), new MetricsConfig { Enabled = true, ExposePort = McpPort }).SetName("Collides_with_Metrics_ExposePort");
    }

    [TestCaseSource(nameof(PortCollisionCases))]
    public void Port_collision_is_rejected(JsonRpcConfig rpc, MetricsConfig? metrics) =>
        AssertInvalid(new McpConfig { Port = McpPort }, rpc, ExitCodes.ConflictingConfigurations, metrics);

    [Test]
    public void Metrics_port_does_not_collide_when_metrics_disabled() =>
        Assert.That(() => McpConfigValidator.Validate(new McpConfig { Port = McpPort }, EnabledRpc(), new MetricsConfig { Enabled = false, ExposePort = McpPort }), Throws.Nothing);

    private static IEnumerable<TestCaseData> NonPositiveLimitCases()
    {
        foreach (long value in new long[] { 0, -1 })
        {
            yield return Limit(nameof(IMcpConfig.MaxRequestBodySize), c => c.MaxRequestBodySize = value, value);
            yield return Limit(nameof(IMcpConfig.MaxConcurrentToolCalls), c => c.MaxConcurrentToolCalls = (int)value, value);
            yield return Limit(nameof(IMcpConfig.ToolTimeout), c => c.ToolTimeout = (int)value, value);
            yield return Limit(nameof(IMcpConfig.MaxResultSize), c => c.MaxResultSize = (int)value, value);
            yield return Limit(nameof(IMcpConfig.MaxLogBlockRange), c => c.MaxLogBlockRange = value, value);
            yield return Limit(nameof(IMcpConfig.MaxLogs), c => c.MaxLogs = (int)value, value);
            yield return Limit(nameof(IMcpConfig.MaxCallGas), c => c.MaxCallGas = value, value);
            yield return Limit(nameof(IMcpConfig.MaxCallDataSize), c => c.MaxCallDataSize = (int)value, value);
        }

        static TestCaseData Limit(string name, Action<McpConfig> set, long value) =>
            new TestCaseData(set).SetName($"Non_positive_{name}_{value}_is_rejected");
    }

    [TestCaseSource(nameof(NonPositiveLimitCases))]
    public void Non_positive_limit_is_rejected(Action<McpConfig> configure)
    {
        McpConfig config = new();
        configure(config);
        AssertInvalid(config, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
    }

    [Test]
    public void ToolTimeout_above_JsonRpc_timeout_is_rejected() =>
        AssertInvalid(new McpConfig { ToolTimeout = 20_001 }, new JsonRpcConfig { Enabled = true, Timeout = 20_000 }, ExitCodes.ConflictingConfigurations);

    [Test]
    public void MaxCallGas_above_JsonRpc_gas_cap_is_rejected() =>
        AssertInvalid(new McpConfig { MaxCallGas = 1_000_001 }, new JsonRpcConfig { Enabled = true, GasCap = 1_000_000 }, ExitCodes.ConflictingConfigurations);

    [Test]
    public void Missing_auth_token_file_is_rejected()
    {
        using TempPath missing = TempPath.GetTempFile();
        AssertInvalid(new McpConfig { AuthTokenFile = missing.Path }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);
    }

    [TestCase("")]
    [TestCase("short-token")]
    [TestCase("   aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa   ")] // 31 characters once trimmed
    public void Short_auth_token_is_rejected_without_leaking_it(string token)
    {
        using TempPath file = TempPath.GetTempFile();
        File.WriteAllText(file.Path, token);

        InvalidConfigurationException exception = AssertInvalid(new McpConfig { AuthTokenFile = file.Path }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);

        if (token.Trim().Length > 0)
            Assert.That(exception.Message, Does.Not.Contain(token.Trim()), "the token must never appear in errors");
    }

    [Test]
    public void Auth_token_is_trimmed_and_accepted()
    {
        using TempPath file = TempPath.GetTempFile();
        File.WriteAllText(file.Path, $"  {ValidToken}\n");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => McpConfigValidator.Validate(new McpConfig { AuthTokenFile = file.Path }, EnabledRpc()), Throws.Nothing);
            Assert.That(McpConfigValidator.LoadAuthToken(file.Path), Is.EqualTo(ValidToken));
        }
    }

    [Test]
    public void Malformed_allowed_origin_is_rejected(
        [Values("*", "localhost:6274", "ftp://localhost", "http://localhost:6274/path", "http://user@localhost:6274",
            "http://localhost:6274?x=1", "http://localhost:6274#frag", "not an origin", "")] string origin) =>
        AssertInvalid(new McpConfig { AllowedOrigins = [origin] }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);

    [TestCase("http://localhost:6274", ExpectedResult = "http://localhost:6274")]
    [TestCase("http://localhost:6274/", ExpectedResult = "http://localhost:6274")]
    [TestCase("https://inspector.example", ExpectedResult = "https://inspector.example")]
    public string Well_formed_allowed_origin_is_normalized(string origin)
    {
        Assert.That(() => McpConfigValidator.Validate(new McpConfig { AllowedOrigins = [origin] }, EnabledRpc()), Throws.Nothing);
        return McpConfigValidator.NormalizeOrigins([origin])[0];
    }

    private static JsonRpcConfig EnabledRpc() => new() { Enabled = true };

    /// <summary>A complete remote-mode config; the token file lives in the certificate's temporary directory.</summary>
    private static McpConfig RemoteConfig(McpTestCertificate certificate)
    {
        string tokenFile = Path.Combine(certificate.Directory, "mcp.token");
        File.WriteAllText(tokenFile, ValidToken);
        return new McpConfig
        {
            Host = "0.0.0.0",
            TlsCertificatePath = certificate.CertificatePath,
            TlsCertificateKeyPath = certificate.KeyPath,
            AuthTokenFile = tokenFile,
            AllowedHosts = ["node.example.com"],
        };
    }

    private static InvalidConfigurationException AssertInvalid(IMcpConfig config, IJsonRpcConfig rpc, int exitCode, IMetricsConfig? metrics = null)
    {
        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() => McpConfigValidator.Validate(config, rpc, metrics))!;
        Assert.That(exception.ExitCode, Is.EqualTo(exitCode));
        return exception;
    }
}
