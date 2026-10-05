// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.Logging;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

[assembly: LoopbackTrace.DumpOnFailure]

namespace Nethermind.BeaconChain.Test.P2P;

// BEACONCHAIN_LOOPBACK_TRACE=1 enables Trace logs and writes failures to the temp directory.
internal static class LoopbackTrace
{
    private static readonly ConcurrentQueue<string> Lines = new();
    private static int _nodes;

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("BEACONCHAIN_LOOPBACK_TRACE") == "1";

    public static ILogManager? NewNode() => Enabled ? new SinkLogManager($"n{Interlocked.Increment(ref _nodes)}") : null;

    public static ILogManager Or(ILogManager fallback, string tag = "pm") => Enabled ? new SinkLogManager(tag) : fallback;

    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class DumpOnFailureAttribute : Attribute, ITestAction
    {
        public ActionTargets Targets => ActionTargets.Test;

        public void BeforeTest(ITest test)
        {
            if (!Enabled) return;
            Lines.Clear();
            Interlocked.Exchange(ref _nodes, 0);
        }

        public void AfterTest(ITest test)
        {
            if (!Enabled || TestContext.CurrentContext.Result.Outcome.Status != TestStatus.Failed) return;
            string path = Path.Combine(Path.GetTempPath(), $"loopback-trace-{test.MethodName ?? "fixture"}-{DateTime.UtcNow:HHmmssfff}.log");
            File.WriteAllLines(path, Lines.ToArray());
            TestContext.Out.WriteLine($"loopback trace: {path}");
        }
    }

    private sealed class SinkLogManager(string tag) : ILogManager
    {
        public ILogger GetClassLogger<T>() => GetLogger(typeof(T).Name);

        public ILogger GetLogger(string loggerName)
        {
            string source = $"{tag} {loggerName[(loggerName.LastIndexOf('.') + 1)..]}";
            return new(new TestLogRecorder(sink: (level, text, ex) =>
                Add(level.ToString()[0], source, ex is null ? text : $"{text} {ex}")));
        }
    }

    private static void Add(char level, string source, string text) =>
        Lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.ffffff} {level} t{Environment.CurrentManagedThreadId} {source}: {text}");
}
