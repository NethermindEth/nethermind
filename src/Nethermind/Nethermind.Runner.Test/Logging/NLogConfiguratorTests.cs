// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Text.Json;
using Nethermind.Runner.Logging;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NUnit.Framework;


namespace Nethermind.Runner.Test.Logging;

[TestFixture, NonParallelizable]
public class NLogConfiguratorTests
{
    private LoggingConfiguration? _previousConfig;

    [SetUp]
    public void SetUp() => _previousConfig = LogManager.Configuration;

    [TearDown]
    public void TearDown() => LogManager.Configuration = _previousConfig;

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

    [TestCase("ecs")]
    [TestCase("gcp")]
    [TestCase("logstash")]
    [TestCase("gelf")]
    public void Ansi_escape_sequences_are_stripped_from_message(string format)
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
