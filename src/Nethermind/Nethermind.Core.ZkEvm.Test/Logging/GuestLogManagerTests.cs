// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Logging;

/// <summary>
/// The non-virtual <see cref="ILogManager.GetClassLogger{T}"/> of <c>ILogManager.zkevm.cs</c>, which the
/// host suite cannot reach: there it is an interface member each manager implements.
/// </summary>
public class GuestLogManagerTests
{
    private static IEnumerable<ILogManager> Managers()
    {
        yield return NullLogManager.Instance;
        yield return LimboLogs.Instance;
        yield return NoErrorLimboLogs.Instance;
        yield return new OneLoggerLogManager(LimboTraceLogger.Instance);
        yield return new TestLogManager();
    }

    [TestCaseSource(nameof(Managers))]
    public void Class_logger_wraps_the_manager_logger(ILogManager manager) =>
        Assert.That(manager.GetClassLogger<GuestLogManagerTests>().UnderlyingLogger,
            Is.SameAs(manager.GetLogger(nameof(GuestLogManagerTests)).UnderlyingLogger));

    [Test]
    public void Class_logger_forwards_to_GetLogger_with_an_empty_name()
    {
        NameRecordingLogManager recorder = new();
        ILogManager manager = recorder;

        manager.GetClassLogger<GuestLogManagerTests>();

        Assert.That(recorder.Names, Is.EqualTo(new[] { string.Empty }));
    }

    private sealed class NameRecordingLogManager : ILogManager
    {
        public List<string> Names { get; } = [];

        public ILogger GetLogger(string loggerName)
        {
            Names.Add(loggerName);
            return LimboTraceLogger.Instance;
        }
    }
}
