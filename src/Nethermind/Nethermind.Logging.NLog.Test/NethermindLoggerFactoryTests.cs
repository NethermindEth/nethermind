// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Logging.Microsoft;
using NUnit.Framework;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace Nethermind.Logging.NLog.Test;

[TestFixture]
public class NethermindLoggerFactoryTests
{
    [Test]
    public void Non_error_log_with_null_exception_keeps_message_unchanged()
    {
        CapturingLogger capturingLogger = new(true);
        using NethermindLoggerFactory factory = CreateFactory(capturingLogger);
        MsLogger logger = factory.CreateLogger(nameof(NethermindLoggerFactoryTests));

        logger.Log(MsLogLevel.Information, default, "message", null, static (state, _) => state);

        Assert.That(capturingLogger.Entries, Has.Count.EqualTo(1));
        Assert.That(capturingLogger.Entries[0].Message, Is.EqualTo("message"));
    }

    [Test]
    public void Disabled_non_error_log_does_not_invoke_formatter()
    {
        CapturingLogger capturingLogger = new(false);
        using NethermindLoggerFactory factory = CreateFactory(capturingLogger);
        MsLogger logger = factory.CreateLogger(nameof(NethermindLoggerFactoryTests));
        int formatterInvocations = 0;

        logger.Log(MsLogLevel.Warning, default, "message", new Exception("details"), (state, _) =>
        {
            formatterInvocations++;
            return state;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(formatterInvocations, Is.Zero);
            Assert.That(capturingLogger.Entries, Is.Empty);
        }
    }

    [TestCase(MsLogLevel.Error)]
    [TestCase(MsLogLevel.Critical)]
    public void Error_log_forwards_original_exception(MsLogLevel logLevel)
    {
        CapturingLogger capturingLogger = new(true);
        using NethermindLoggerFactory factory = CreateFactory(capturingLogger);
        MsLogger logger = factory.CreateLogger(nameof(NethermindLoggerFactoryTests));
        InvalidOperationException exception = new("outer", new ArgumentException("inner"));

        logger.Log(logLevel, default, "message", exception, static (state, _) => state);

        Assert.That(capturingLogger.Entries, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(capturingLogger.Entries[0].Message, Is.EqualTo("message"));
            Assert.That(capturingLogger.Entries[0].Exception, Is.SameAs(exception));
        }
    }

    [Test]
    public void Non_error_log_includes_exception_details(
        [Values(MsLogLevel.Warning, MsLogLevel.Information, MsLogLevel.Debug, MsLogLevel.Trace)] MsLogLevel logLevel,
        [Values] bool lowerLogLevel)
    {
        CapturingLogger capturingLogger = new(true);
        using NethermindLoggerFactory factory = CreateFactory(capturingLogger, lowerLogLevel);
        MsLogger logger = factory.CreateLogger(nameof(NethermindLoggerFactoryTests));
        InvalidOperationException exception = new("outer", new ArgumentException("inner"));

        logger.Log(logLevel, default, "message", exception, static (state, _) => state);

        Assert.That(capturingLogger.Entries, Has.Count.EqualTo(1));
        LogEntry entry = capturingLogger.Entries[0];
        string expectedMessage = string.Concat("message", Environment.NewLine, exception.ToString());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(ExpectedLevel(logLevel, lowerLogLevel)));
            Assert.That(entry.Message, Is.EqualTo(expectedMessage));
            Assert.That(entry.Exception, Is.Null);
        }
    }

    private static NethermindLoggerFactory CreateFactory(CapturingLogger logger, bool lowerLogLevel = false) =>
        new(new OneLoggerLogManager(new ILogger(logger)), lowerLogLevel);

    private static CapturedLevel ExpectedLevel(MsLogLevel logLevel, bool lowerLogLevel)
    {
        if (!lowerLogLevel)
        {
            return logLevel switch
            {
                MsLogLevel.Warning => CapturedLevel.Warning,
                MsLogLevel.Information => CapturedLevel.Information,
                MsLogLevel.Debug => CapturedLevel.Debug,
                MsLogLevel.Trace => CapturedLevel.Trace,
                _ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null),
            };
        }

        return logLevel switch
        {
            MsLogLevel.Warning => CapturedLevel.Information,
            MsLogLevel.Information => CapturedLevel.Trace,
            MsLogLevel.Debug => CapturedLevel.Trace,
            MsLogLevel.Trace => CapturedLevel.Trace,
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel), logLevel, null),
        };
    }

    private enum CapturedLevel
    {
        Information,
        Warning,
        Debug,
        Trace,
        Error,
    }

    private sealed record LogEntry(CapturedLevel Level, string Message, Exception Exception = null);

    private sealed class CapturingLogger(bool enabled) : InterfaceLogger
    {
        public List<LogEntry> Entries { get; } = [];

        public bool IsError => enabled;
        public bool IsWarn => enabled;
        public bool IsInfo => enabled;
        public bool IsDebug => enabled;
        public bool IsTrace => enabled;

        public void Info(string text) => Entries.Add(new(CapturedLevel.Information, text));
        public void Warn(string text) => Entries.Add(new(CapturedLevel.Warning, text));
        public void Debug(string text) => Entries.Add(new(CapturedLevel.Debug, text));
        public void Trace(string text) => Entries.Add(new(CapturedLevel.Trace, text));
        public void Error(string text, Exception exception = null) => Entries.Add(new(CapturedLevel.Error, text, exception));
    }
}
