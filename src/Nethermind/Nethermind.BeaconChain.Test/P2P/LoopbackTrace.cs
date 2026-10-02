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

/// <summary>
/// Every log line of the loopback nodes a test creates, libp2p's own included, kept in memory and written to a file only when the test fails.
/// </summary>
/// <remarks>Off unless <c>BEACONCHAIN_LOOPBACK_TRACE=1</c>; tests then run with Trace logging, which is slower. Files go to the temp directory.</remarks>
internal static class LoopbackTrace
{
    private static readonly ConcurrentQueue<string> Lines = new();
    private static int _nodes;

    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("BEACONCHAIN_LOOPBACK_TRACE") == "1";

    /// <summary>A log manager tagging each line with a new node number; <c>null</c> when tracing is off.</summary>
    public static ILogManager? NewNode() => Enabled ? new SinkLogManager($"n{Interlocked.Increment(ref _nodes)}") : null;

    /// <summary>A log manager under <paramref name="tag"/> when tracing is on, otherwise <paramref name="fallback"/>.</summary>
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

        public ILogger GetLogger(string loggerName) => new(new Sink($"{tag} {loggerName[(loggerName.LastIndexOf('.') + 1)..]}"));
    }

    private sealed class Sink(string source) : InterfaceLogger
    {
        public bool IsInfo => true;
        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsTrace => true;
        public bool IsError => true;

        private static void Add(char level, string source, string text) =>
            Lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.ffffff} {level} t{Environment.CurrentManagedThreadId} {source}: {text}");

        public void Info(string text) => Add('I', source, text);
        public void Warn(string text) => Add('W', source, text);
        public void Debug(string text) => Add('D', source, text);
        public void Trace(string text) => Add('T', source, text);
        public void Error(string text, Exception? ex = null) => Add('E', source, ex is null ? text : $"{text} {ex}");
    }
}
