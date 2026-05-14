// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Runner.Logging;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NUnit.Framework;

#nullable enable

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

        memory.Logs.Should().ContainSingle();
        using JsonDocument doc = JsonDocument.Parse(memory.Logs[0]);
        doc.RootElement.GetProperty(key).GetString().Should().Be(expectedValue);
    }

    [Test]
    public void Gcp_severity_mapping_is_spec_accurate()
    {
        MemoryTarget memory = SetUpAndConfigure("gcp");

        Logger logger = LogManager.GetLogger("t");
        logger.Trace("t"); logger.Debug("d"); logger.Info("i");
        logger.Warn("w"); logger.Error("e"); logger.Fatal("f");

        string[] severities =
        [
            ParseSeverity(memory.Logs[0]), ParseSeverity(memory.Logs[1]), ParseSeverity(memory.Logs[2]),
            ParseSeverity(memory.Logs[3]), ParseSeverity(memory.Logs[4]), ParseSeverity(memory.Logs[5])
        ];

        severities.Should().Equal("DEBUG", "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL");

        static string ParseSeverity(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("severity").GetString()!;
        }
    }

    [Test]
    public void Gelf_level_mapping_uses_syslog_severity()
    {
        MemoryTarget memory = SetUpAndConfigure("gelf");

        Logger logger = LogManager.GetLogger("t");
        logger.Trace("t"); logger.Debug("d"); logger.Info("i");
        logger.Warn("w"); logger.Error("e"); logger.Fatal("f");

        int[] levels =
        [
            ParseLevel(memory.Logs[0]), ParseLevel(memory.Logs[1]), ParseLevel(memory.Logs[2]),
            ParseLevel(memory.Logs[3]), ParseLevel(memory.Logs[4]), ParseLevel(memory.Logs[5])
        ];

        levels.Should().Equal(7, 7, 6, 4, 3, 2);

        static int ParseLevel(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("level").GetInt32();
        }
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

        consoleTarget.Layout.Should().BeSameAs(originalLayout);
    }

    [Test]
    public void Unknown_format_throws_ArgumentException()
    {
        Action act = () => NLogConfigurator.ConfigureConsoleFormat("xml");
        act.Should().Throw<ArgumentException>().WithMessage("*xml*");
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

        fileTarget.Layout.Should().BeSameAs(fileLayoutBefore);
        consoleTarget.Layout.Should().NotBeSameAs(fileLayoutBefore);
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
