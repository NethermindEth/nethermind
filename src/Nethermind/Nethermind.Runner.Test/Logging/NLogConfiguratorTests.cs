// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Logging.NLog;
using Nethermind.Runner.Logging;
using Nethermind.Seq.Config;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Seq;
using NSubstitute;
using NUnit.Framework;
using ILogger = Nethermind.Logging.ILogger;
using InterfaceLogger = Nethermind.Logging.InterfaceLogger;


namespace Nethermind.Runner.Test.Logging;

[TestFixture, NonParallelizable]
public class NLogConfiguratorTests
{
    private readonly List<IDisposable> _reloadSubscriptions = [];
    private LoggingConfiguration? _previousConfig;
    private Nethermind.Logging.ILogManager _previousStaticLogManager = null!;
    private string _logDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _previousConfig = LogManager.Configuration;
        _previousStaticLogManager = Nethermind.Logging.Static.LogManager;
        _logDirectory = Path.Combine(Path.GetTempPath(), $"{nameof(NLogConfiguratorTests)}-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        _reloadSubscriptions.ForEach(static subscription => subscription.Dispose());
        _reloadSubscriptions.Clear();
        LogManager.Configuration = _previousConfig;
        Nethermind.Logging.Static.LogManager = _previousStaticLogManager;
        if (Directory.Exists(_logDirectory)) Directory.Delete(_logDirectory, recursive: true);
    }

    [TestCase("ecs", "log.level", "info")]
    [TestCase("logstash", "level", "INFO")]
    [TestCase("gelf", "short_message", "hello world")]
    public void ConfigureConsoleFormat_writes_format_specific_keys(string format, string key, string expectedValue)
    {
        MemoryTarget memory = SetUpAndConfigure(format);

        LogManager.GetLogger("t").Info("hello world");

        Assert.That(memory.Logs, Has.Count.EqualTo(1));
        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        Assert.That(doc.RootElement.GetProperty(key).GetString(), Is.EqualTo(expectedValue));
    }

    private static readonly (string Format, string Field, string[] Expected)[] LevelMappingCases =
    {
        ("gcp", "severity", new[] { "DEBUG", "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL" }),
        ("gelf", "level", new[] { "7", "7", "6", "4", "3", "2" })
    };

    [TestCaseSource(nameof(LevelMappingCases))]
    public void Level_mapping_is_spec_accurate((string Format, string Field, string[] Expected) testCase)
    {
        MemoryTarget memory = SetUpAndConfigure(testCase.Format);

        Logger logger = LogManager.GetLogger("t");
        logger.Trace("t"); logger.Debug("d"); logger.Info("i");
        logger.Warn("w"); logger.Error("e"); logger.Fatal("f");

        string[] actual = new string[6];
        for (int i = 0; i < 6; i++)
        {
            using JsonDocument doc = JsonDocument.Parse(memory.Logs[i]);
            JsonElement value = doc.RootElement.GetProperty(testCase.Field);
            // GCP severity is a string; GELF level is a JSON number.
            actual[i] = value.ValueKind == JsonValueKind.Number ? value.GetInt32().ToString() : value.GetString()!;
        }

        Assert.That(actual, Is.EqualTo(testCase.Expected));
    }

    [Test]
    public void Gelf_timestamp_is_numeric_seconds_since_epoch()
    {
        MemoryTarget memory = SetUpAndConfigure("gelf");

        DateTime before = DateTime.UtcNow;
        LogManager.GetLogger("t").Info("hello");
        DateTime after = DateTime.UtcNow;

        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        JsonElement ts = doc.RootElement.GetProperty("timestamp");

        // Spec violation guard: GELF 1.1 requires timestamp as numeric seconds-since-epoch, not an ISO string.
        Assert.That(ts.ValueKind, Is.EqualTo(JsonValueKind.Number));

        double seconds = ts.GetDouble();
        double lower = (before.AddSeconds(-1) - DateTime.UnixEpoch).TotalSeconds;
        double upper = (after.AddSeconds(1) - DateTime.UnixEpoch).TotalSeconds;
        Assert.That(seconds, Is.InRange(lower, upper));
    }

    [Test]
    public void Logstash_version_is_numeric_one()
    {
        MemoryTarget memory = SetUpAndConfigure("logstash");

        LogManager.GetLogger("t").Info("hello");

        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        JsonElement version = doc.RootElement.GetProperty("@version");

        // Spec violation guard: logstash-logback-encoder defines @version as integer 1, not "1".
        Assert.That(version.ValueKind, Is.EqualTo(JsonValueKind.Number));
        Assert.That(version.GetInt32(), Is.EqualTo(1));
    }

    [TestCase("ecs", "@timestamp")]
    [TestCase("logstash", "@timestamp")]
    [TestCase("gcp", "time")]
    public void Iso_timestamp_is_string_with_subsecond_precision(string format, string field)
    {
        MemoryTarget memory = SetUpAndConfigure(format);

        LogManager.GetLogger("t").Info("hello");

        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        JsonElement ts = doc.RootElement.GetProperty(field);
        Assert.That(ts.ValueKind, Is.EqualTo(JsonValueKind.String));
        string value = ts.GetString()!;
        // .fffffffZ -> 7 fractional digits before the Z. Don't assert exact value, only shape.
        Assert.That(value, Does.Match(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$"));
    }

    [Test]
    public void Ansi_escape_sequences_are_stripped_from_message([Values("ecs", "gcp", "logstash", "gelf")] string format)
    {
        MemoryTarget memory = SetUpAndConfigure(format);

        // Two SGR sequences wrapping the visible text "colored".
        LogManager.GetLogger("t").Info("\x1B[31mcolored\x1B[0m");

        string messageField = format switch
        {
            "gelf" => "short_message",
            _ => "message"
        };

        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        string value = doc.RootElement.GetProperty(messageField).GetString()!;
        Assert.That(value, Is.EqualTo("colored"));
    }

    [Test]
    public void Ecs_omits_error_fields_when_no_exception()
    {
        MemoryTarget memory = SetUpAndConfigure("ecs");

        LogManager.GetLogger("t").Info("plain");

        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        Assert.That(doc.RootElement.TryGetProperty("error.type", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("error.message", out _), Is.False);
        Assert.That(doc.RootElement.TryGetProperty("error.stack_trace", out _), Is.False);
    }

    [Test]
    public void Plain_format_leaves_layout_untouched()
    {
        ConsoleTarget consoleTarget = new("memory-console") { Layout = "${message}" };
        LoggingConfiguration config = new();
        config.AddTarget(consoleTarget);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, consoleTarget);
        LogManager.Configuration = config;

        Layout originalLayout = consoleTarget.Layout;
        NLogConfigurator.ConfigureConsoleFormat("plain");

        Assert.That(consoleTarget.Layout, Is.SameAs(originalLayout));
    }

    [Test]
    public void Unknown_format_throws_ArgumentException()
    {
        Action act = () => NLogConfigurator.ConfigureConsoleFormat("xml");
        Assert.That(act, Throws.TypeOf<ArgumentException>().With.Message.Contains("xml"));
    }

    [Test]
    public void File_target_layout_is_not_replaced()
    {
        ConsoleTarget consoleTarget = new("memory-console") { Layout = "${message}" };
        FileTarget fileTarget = new("file") { FileName = "ignored.log", Layout = "${longdate}|${level}|${message}" };
        Layout fileLayoutBefore = fileTarget.Layout;

        LoggingConfiguration config = new();
        config.AddTarget(consoleTarget);
        config.AddTarget(fileTarget);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, consoleTarget);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, fileTarget);
        LogManager.Configuration = config;

        NLogConfigurator.ConfigureConsoleFormat("ecs");

        Assert.That(fileTarget.Layout, Is.SameAs(fileLayoutBefore));
        Assert.That(consoleTarget.Layout, Is.Not.SameAs(fileLayoutBefore));
    }

    [Test]
    public void Failure_while_reapplying_overrides_is_logged_instead_of_thrown()
    {
        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        interfaceLogger.IsError.Returns(true);
        bool failing = false;
        ISeqConfig seqConfig = Substitute.For<ISeqConfig>();
        seqConfig.MinLevel.Returns(_ => failing ? throw new InvalidOperationException("seq") : "Info");
        StartUp("Warn", "ecs", seqConfig, null, new ILogger(interfaceLogger));
        failing = true;

        Assert.DoesNotThrow(static () => LogManager.Configuration = LoadShippedConfiguration());

        interfaceLogger.Received().Error(Arg.Any<string>(), Arg.Is<Exception>(static ex => ex is InvalidOperationException));
        // The steps ahead of the failing one still ran.
        LoggingRule consoleRule = LogManager.Configuration!.LoggingRules.Single(static rule =>
            rule.LoggerNamePattern == "*" && rule.Targets.Any(static target => target.Name == "auto-colored-console-async"));
        Assert.That(consoleRule.Levels.Select(static level => level.Name), Is.EqualTo(new[] { "Warn", "Error", "Fatal" }));
    }

    [Test]
    public void Invalid_console_format_is_not_retried_on_configuration_reload()
    {
        InterfaceLogger interfaceLogger = Substitute.For<InterfaceLogger>();
        interfaceLogger.IsError.Returns(true);
        StartUp(null, "xml", new SeqConfig(), null, new ILogger(interfaceLogger));
        interfaceLogger.ClearReceivedCalls();

        LogManager.Configuration = LoadShippedConfiguration();

        interfaceLogger.DidNotReceive().Error(Arg.Any<string>(), Arg.Any<Exception>());
    }

    [Test]
    public void Overrides_are_not_reapplied_when_the_configuration_is_cleared()
    {
        LogManager.Configuration = LoadShippedConfiguration();
        _reloadSubscriptions.Add(NLogConfigurator.ConfigureCommandLineOverrides("Warn", "plain", default));

        // As on shutdown: the event carries no configuration, and NLog loads NLog.config again on the next read.
        LogManager.Configuration = null;

        LoggingRule consoleRule = LogManager.Configuration!.LoggingRules.Single(static rule =>
            rule.LoggerNamePattern == "*" && rule.Targets.Any(static target => target.Name == "auto-colored-console-async"));
        Assert.That(consoleRule.Levels.Select(static level => level.Name), Is.EqualTo(new[] { "Info", "Warn", "Error", "Fatal" }));
    }

    [TestCase("Info", "Synchronization.*:Debug", new[] { "Debug", "Info", "Warn", "Error", "Fatal" })]
    [TestCase("Warn", "Synchronization.*:Debug", new[] { "Debug", "Info", "Warn", "Error", "Fatal" })]
    [TestCase("Info", "Synchronization.*:Error", new[] { "Error", "Fatal" })]
    public void Init_log_rules_keep_their_levels_after_configuration_reload(string logLevel, string logRules, string[] expectedLevels)
    {
        ReloadShippedConfiguration(logLevel, "plain", new SeqConfig(), logRules: logRules);

        LoggingRule rule = LogManager.Configuration!.LoggingRules.Single(static rule => rule.LoggerNamePattern == "Synchronization.*");
        Assert.That(rule.Levels.Select(static level => level.Name), Is.EqualTo(expectedLevels));
    }

    private static IEnumerable<TestCaseData> ReloadScenarios()
    {
        SeqConfig seqConfig = new() { MinLevel = "Info", ServerUrl = "http://seq.example:5341", ApiKey = "k3y" };

        yield return new TestCaseData(null, "xml", null, seqConfig, false)
            .SetArgDisplayNames("invalid console format with seq enabled");
        yield return new TestCaseData("Warn", "ecs", "Synchronization.*:Debug;Network.*:Error", seqConfig, false)
            .SetArgDisplayNames("every override");
        yield return new TestCaseData("Warn", "ecs", "Synchronization.*:Debug;Network.*:Error", seqConfig, true)
            .SetArgDisplayNames("every override with a later NLogManager subscribed");
        yield return new TestCaseData("Error", "plain", null, new SeqConfig { MinLevel = "Warn" }, false)
            .SetArgDisplayNames("log level with a seq floor");
        yield return new TestCaseData(null, "plain", null, new SeqConfig { MinLevel = "Off" }, false)
            .SetArgDisplayNames("seq disabled");
    }

    [TestCaseSource(nameof(ReloadScenarios))]
    public void Configuration_reload_restores_the_state_left_by_startup(string? logLevel, string format, string? logRules, SeqConfig seqConfig, bool withLaterNLogManager)
    {
        StartUp(logLevel, format, seqConfig, logRules);
        if (withLaterNLogManager) _reloadSubscriptions.Add(new NLogManager("critical.log", _logDirectory));
        string[] afterStartup = DescribeConfiguration();

        LogManager.Configuration = LoadShippedConfiguration();

        Assert.That(DescribeConfiguration(), Is.EqualTo(afterStartup));
    }

    private static string[] DescribeConfiguration()
    {
        LoggingConfiguration configuration = LogManager.Configuration!;
        IEnumerable<string> rules = configuration.LoggingRules.Select(static rule =>
            $"rule {rule.LoggerNamePattern} -> {string.Join(",", rule.Targets.Select(static target => target.Name))} final={rule.Final} levels={string.Join(",", rule.Levels)}");
        IEnumerable<string> targets = configuration.AllTargets.Select(static target => target switch
        {
            SeqTarget seq => $"seq {seq.Name} url={seq.ServerUrl} key={seq.ApiKey}",
            FileTarget file => $"file {file.Name} {file.FileName}",
            TargetWithLayout withLayout => $"{withLayout.GetType().Name} {withLayout.Name} {withLayout.Layout.GetType().Name}",
            _ => $"{target.GetType().Name} {target.Name}"
        });
        return rules.Concat(targets).ToArray();
    }

    private void ReloadShippedConfiguration(string? logLevel, string format, ISeqConfig seqConfig, ILogger logger = default, string? logRules = null)
    {
        StartUp(logLevel, format, seqConfig, logRules, logger);

        // Replacing the configuration raises ConfigurationChanged, exactly as an autoReload does.
        LogManager.Configuration = LoadShippedConfiguration();
    }

    /// <summary>Applies the logging configuration in the order <c>Program</c> does at startup.</summary>
    private void StartUp(string? logLevel, string format, ISeqConfig seqConfig, string? logRules, ILogger logger = default)
    {
        LogManager.Configuration = LoadShippedConfiguration();
        _reloadSubscriptions.Add(NLogConfigurator.ConfigureCommandLineOverrides(logLevel, format, logger));
        _reloadSubscriptions.Add(new NLogManager("nethermind.log", _logDirectory, logRules));
        _reloadSubscriptions.Add(NLogConfigurator.ConfigureSeq(seqConfig, logger));
    }

    private static LoggingConfiguration LoadShippedConfiguration() =>
        new XmlLoggingConfiguration(Path.Combine(AppContext.BaseDirectory, "NLog.config"));

    private static MemoryTarget SetUpAndConfigure(string format)
    {
        ConsoleTarget consoleTarget = new("memory-console") { Layout = "${message}" };
        MemoryTarget memory = new("memory-buffer");

        LoggingConfiguration config = new();
        config.AddTarget(consoleTarget);
        config.AddTarget(memory);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, consoleTarget);
        config.AddRule(LogLevel.Trace, LogLevel.Fatal, memory);
        LogManager.Configuration = config;

        NLogConfigurator.ConfigureConsoleFormat(format);

        // Mirror the freshly set JsonLayout to the memory target so we can read the rendered output.
        memory.Layout = consoleTarget.Layout;
        LogManager.ReconfigExistingLoggers();

        return memory;
    }
}
