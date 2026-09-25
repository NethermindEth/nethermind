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
    public void Non_loopback_or_unparseable_host_is_rejected(
        [Values("0.0.0.0", "192.168.1.10", "10.0.0.1", "::", "::ffff:192.168.1.10", "localhost", "evil.com", "", " ")] string host) =>
        AssertInvalid(new McpConfig { Host = host }, EnabledRpc(), ExitCodes.ForbiddenOptionValue);

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

    private static InvalidConfigurationException AssertInvalid(IMcpConfig config, IJsonRpcConfig rpc, int exitCode, IMetricsConfig? metrics = null)
    {
        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(() => McpConfigValidator.Validate(config, rpc, metrics))!;
        Assert.That(exception.ExitCode, Is.EqualTo(exitCode));
        return exception;
    }
}
