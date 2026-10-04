// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac;
using Nethermind.BeaconChain.Diagnostics;
using Nethermind.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Diagnostics;

public class ProcessStallWatchdogTests
{
    [Test]
    public void Delayed_tick_reports_once_with_collection_and_pause_deltas([Values] bool gen2)
    {
        using Context context = new();
        context.Watchdog.Tick();
        context.RunProbes();
        context.Now = TimeSpan.FromSeconds(12);
        context.Sample = new(TimeSpan.FromSeconds(3), 4, 2, gen2 ? 1 : 0, 0, 8, 10);
        context.Watchdog.Tick();
        context.RunProbes();
        context.Now += TimeSpan.FromSeconds(1);
        context.Watchdog.Tick();

        Assert.That(context.Info, Is.EqualTo(new[]
        {
            $"Process paused, the watchdog thread did not run for 12.0 s; GC pause 3.000 s; GC collections gen0 4, gen1 2, gen2 {(gen2 ? 1 : 0)}; gen2 collected: {gen2}"
        }));
    }

    [Test]
    public void Pending_probe_reports_once_and_recovers_with_total_wait()
    {
        using Context context = new();
        context.Watchdog.Tick();
        for (int second = 1; second <= 9; second++)
        {
            context.Now = TimeSpan.FromSeconds(second);
            context.Sample = new(TimeSpan.Zero, 0, 0, 0, second, 8, second * 2);
            context.Watchdog.Tick();
        }
        context.RunProbes();
        context.Now = TimeSpan.FromSeconds(10);
        context.Watchdog.Tick();

        Assert.That(context.Info, Is.EqualTo(new[]
        {
            "Thread pool starved, queued work waited 6.0 s; pending work items 6; thread count 8; completed work items delta 12",
            "Thread pool starvation ended after 9.0 s"
        }));
    }

    [Test]
    public void Completed_delayed_probe_is_reported_even_before_the_next_tick()
    {
        using Context context = new();
        context.Watchdog.Tick();
        for (int second = 1; second <= 5; second++)
        {
            context.Now = TimeSpan.FromSeconds(second);
            context.Watchdog.Tick();
        }
        context.Now = TimeSpan.FromSeconds(5.5);
        context.RunProbes();
        context.Now = TimeSpan.FromSeconds(6);
        context.Watchdog.Tick();

        Assert.That(context.Info, Is.EqualTo(new[]
        {
            "Thread pool starved, queued work waited 5.5 s; pending work items 0; thread count 0; completed work items delta 0",
            "Thread pool starvation ended after 5.5 s"
        }));
    }

    [Test]
    public void Healthy_ticks_produce_no_lines()
    {
        using Context context = new();
        for (int second = 0; second < 20; second++)
        {
            context.Now = TimeSpan.FromSeconds(second);
            context.Watchdog.Tick();
            context.RunProbes();
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(context.Info, Is.Empty);
        Assert.That(context.Debug, Is.Empty);
        Assert.That(context.Scheduled, Is.EqualTo(20));
    }

    [Test]
    public void Summary_reports_interval_maxima_and_resets_them()
    {
        using Context context = new(summaryInterval: TimeSpan.FromSeconds(3));
        for (int second = 0; second <= 6; second++)
        {
            context.Now = TimeSpan.FromSeconds(second);
            context.Sample = new(TimeSpan.FromMilliseconds(second), 0, 0, 0, 0, 0, 0);
            context.Watchdog.Tick();
            context.Now += TimeSpan.FromMilliseconds(second < 3 ? 200 : 100);
            context.RunProbes();
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(context.Debug, Has.Count.EqualTo(2));
        Assert.That(context.Debug[0], Does.StartWith("Process stall watchdog summary: max tick delay 1.000 s; max probe latency 0.200 s;"));
        Assert.That(context.Debug[1], Does.StartWith("Process stall watchdog summary: max tick delay 1.000 s; max probe latency 0.100 s;"));
        Assert.That(context.Debug, Has.All.Contains("GC pause 0.003 s; working set "));
        Assert.That(context.Info, Is.Empty);
    }

    [Test]
    public void Dispose_joins_the_dedicated_thread_and_prevents_restart()
    {
        using ManualResetEventSlim waiting = new();
        using Context context = new(waitForTick: (stop, _) =>
        {
            waiting.Set();
            return stop.WaitOne();
        });
        context.Watchdog.Start();
        Assert.That(waiting.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(context.Watchdog.IsRunning, Is.True);
        context.Watchdog.Dispose();
        context.Watchdog.Dispose();
        context.Watchdog.Start();

        Assert.That(context.Watchdog.IsRunning, Is.False);
    }

    [Test]
    public void Failed_tick_logs_debug_and_the_thread_keeps_running()
    {
        using ManualResetEventSlim waiting = new();
        int attempts = 0;
        using Context context = new(waitForTick: (stop, _) =>
        {
            waiting.Set();
            return stop.WaitOne();
        }, sampleSource: () =>
        {
            if (++attempts == 1) throw new InvalidOperationException();
            return default;
        });
        context.Watchdog.Start();
        Assert.That(waiting.Wait(TimeSpan.FromSeconds(5)), Is.True);
        context.Watchdog.Dispose();

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(context.Debug, Is.EqualTo(new[] { "Process stall watchdog tick failed." }));
        Assert.That(context.Info, Is.Empty);
    }

    private sealed class Context : IDisposable
    {
        private readonly IContainer _container;
        private readonly Queue<(Action<ProcessStallWatchdog.Probe> Callback, ProcessStallWatchdog.Probe Probe)> _probes = new();
        internal readonly List<string> Info = [];
        internal readonly List<string> Debug = [];
        internal TimeSpan Now;
        internal ProcessStallWatchdog.RuntimeSample Sample;
        internal int Scheduled;
        internal ProcessStallWatchdog Watchdog { get; }

        internal Context(TimeSpan? summaryInterval = null, Func<WaitHandle, TimeSpan, bool>? waitForTick = null,
            Func<ProcessStallWatchdog.RuntimeSample>? sampleSource = null)
        {
            InterfaceLogger logger = Substitute.For<InterfaceLogger>();
            logger.IsInfo.Returns(true);
            logger.IsDebug.Returns(true);
            logger.When(x => x.Info(Arg.Any<string>())).Do(call => Info.Add(call.Arg<string>()));
            logger.When(x => x.Debug(Arg.Any<string>())).Do(call => Debug.Add(call.Arg<string>()));
            _container = BeaconChainTestContainer.Builder(logManager: new OneLoggerLogManager(new ILogger(logger)))
                .AddSingleton<ProcessStallWatchdog, ILogManager>(logManager => new ProcessStallWatchdog(logManager)
                {
                    Clock = () => Now,
                    Sample = sampleSource ?? (() => Sample),
                    ScheduleProbe = (callback, probe) =>
                    {
                        Scheduled++;
                        _probes.Enqueue((callback, probe));
                    },
                    SummaryInterval = summaryInterval ?? TimeSpan.FromMinutes(1),
                    WaitForTick = waitForTick ?? ((stop, interval) => stop.WaitOne(interval))
                }).Build();
            Watchdog = _container.Resolve<ProcessStallWatchdog>();
        }

        internal void RunProbes()
        {
            while (_probes.TryDequeue(out (Action<ProcessStallWatchdog.Probe> Callback, ProcessStallWatchdog.Probe Probe) item))
            {
                item.Callback(item.Probe);
            }
        }

        public void Dispose() => _container.Dispose();
    }
}
