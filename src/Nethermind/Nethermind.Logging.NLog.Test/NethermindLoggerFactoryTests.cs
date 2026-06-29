// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Logging.Microsoft;
using NUnit.Framework;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;

namespace Nethermind.Logging.NLog.Test;

[TestFixture]
public class NethermindLoggerFactoryTests
{
    [Test]
    public void Logger_name_matches_nethermind_class_logger_convention()
    {
        TestLogManager logManager = new();
        using NethermindLoggerFactory loggerFactory = new(logManager);

        _ = loggerFactory.CreateLogger("Nethermind.Kademlia.KBucketTree");

        Assert.That(logManager.LastLoggerName, Is.EqualTo("Kademlia.KBucketTree"));
    }

    [Test]
    public void Log_error_forwards_exception()
    {
        TestLogManager logManager = new();
        using NethermindLoggerFactory loggerFactory = new(logManager);
        MicrosoftLogger logger = loggerFactory.CreateLogger("Nethermind.Kademlia.Kademlia");
        Exception exception = new("test");

        logger.LogError(exception, "Bootstrap iteration failed.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logManager.Logger.LastErrorText, Is.EqualTo("Bootstrap iteration failed."));
            Assert.That(logManager.Logger.LastErrorException, Is.SameAs(exception));
        }
    }

    private sealed class TestLogManager : ILogManager
    {
        public TestLogger Logger { get; } = new();

        public string? LastLoggerName { get; private set; }

        public ILogger GetClassLogger<T>() => GetLogger(ILogManager.GetLoggerName(typeof(T)));

        public ILogger GetLogger(string loggerName)
        {
            LastLoggerName = loggerName;
            return new(Logger);
        }
    }

    private sealed class TestLogger : InterfaceLogger
    {
        public string? LastErrorText { get; private set; }

        public Exception? LastErrorException { get; private set; }

        public bool IsInfo => true;

        public bool IsWarn => true;

        public bool IsDebug => true;

        public bool IsTrace => true;

        public bool IsError => true;

        public void Info(string text) { }

        public void Warn(string text) { }

        public void Debug(string text) { }

        public void Trace(string text) { }

        public void Error(string text, Exception? ex = null)
        {
            LastErrorText = text;
            LastErrorException = ex;
        }
    }
}
