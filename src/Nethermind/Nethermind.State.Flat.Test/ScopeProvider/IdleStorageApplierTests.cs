// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.ScopeProvider;

public class IdleStorageApplierTests
{
    private const int Tid = 4242;
    private const int Eperm = 1;
    private const int Eacces = 13;

    [TestCase(EarlyStorageApplyPriority.Idle, new[] { "sched_setscheduler(5)" }, "SCHED_IDLE")]
    [TestCase(EarlyStorageApplyPriority.Low, new[] { "sched_setscheduler(0)", "gettid", "setpriority(4242, 10)" }, "SCHED_OTHER nice 10, tid 4242")]
    [TestCase(EarlyStorageApplyPriority.Normal, new[] { "sched_setscheduler(0)", "gettid", "setpriority(4242, 0)" }, "SCHED_OTHER nice 0, tid 4242")]
    public void SetPriority_OnLinux_SetsThePolicyAndTheNiceOfTheThread(EarlyStorageApplyPriority priority, string[] expectedCalls, string expectedPolicy)
    {
        RecordingCalls calls = new(isLinux: true);
        TestLogger logger = new();

        string policy = IdleStorageApplier.SetPriority(priority, calls, new ILogger(logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Calls, Is.EqualTo(expectedCalls));
            Assert.That(policy, Is.EqualTo(expectedPolicy));
            Assert.That(logger.LogList, Is.Empty);
        }
    }

    [TestCase(EarlyStorageApplyPriority.Idle, ThreadPriority.Lowest)]
    [TestCase(EarlyStorageApplyPriority.Low, ThreadPriority.BelowNormal)]
    [TestCase(EarlyStorageApplyPriority.Normal, ThreadPriority.Normal)]
    public void SetPriority_ElsewhereSetsTheThreadPriority(EarlyStorageApplyPriority priority, ThreadPriority expected)
    {
        RecordingCalls calls = new(isLinux: false);
        TestLogger logger = new();

        string policy = IdleStorageApplier.SetPriority(priority, calls, new ILogger(logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Calls, Is.EqualTo(new[] { $"ThreadPriority.{expected}" }));
            Assert.That(policy, Is.EqualTo($"thread priority {expected}"));
            Assert.That(logger.LogList, Is.Empty);
        }
    }

    [TestCase(EarlyStorageApplyPriority.Idle, Eperm, 0, Tid, ThreadPriority.Lowest, new[] { "sched_setscheduler(5)" })]
    [TestCase(EarlyStorageApplyPriority.Low, Eperm, 0, Tid, ThreadPriority.BelowNormal, new[] { "sched_setscheduler(0)" })]
    [TestCase(EarlyStorageApplyPriority.Low, 0, 0, -1, ThreadPriority.BelowNormal, new[] { "sched_setscheduler(0)", "gettid" })]
    [TestCase(EarlyStorageApplyPriority.Low, 0, Eacces, Tid, ThreadPriority.BelowNormal, new[] { "sched_setscheduler(0)", "gettid", "setpriority(4242, 10)" })]
    [TestCase(EarlyStorageApplyPriority.Normal, Eperm, 0, Tid, ThreadPriority.Normal, new[] { "sched_setscheduler(0)" })]
    [TestCase(EarlyStorageApplyPriority.Normal, 0, Eacces, Tid, ThreadPriority.Normal, new[] { "sched_setscheduler(0)", "gettid", "setpriority(4242, 0)" })]
    public void SetPriority_WhenALinuxCallFails_WarnsOnceAndSetsTheThreadPriority(
        EarlyStorageApplyPriority priority, int policyErrno, int niceErrno, int tid, ThreadPriority fallback, string[] linuxCalls)
    {
        RecordingCalls calls = new(isLinux: true, policyErrno, niceErrno, tid);
        TestLogger logger = new();
        string[] expectedCalls = [.. linuxCalls, $"ThreadPriority.{fallback}"];

        string policy = IdleStorageApplier.SetPriority(priority, calls, new ILogger(logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls.Calls, Is.EqualTo(expectedCalls));
            Assert.That(policy, Is.EqualTo($"thread priority {fallback}"));
            Assert.That(logger.LogList, Has.Count.EqualTo(1));
            int errno = policyErrno + niceErrno;
            if (errno != 0) Assert.That(logger.LogList[0], Does.Contain($"errno {errno}"));
        }
    }

    [Test]
    public void SetPriority_WhenTheCallsThrow_WarnsAndCarriesOn([Values] EarlyStorageApplyPriority priority, [Values] bool isLinux)
    {
        TestLogger logger = new();

        string policy = IdleStorageApplier.SetPriority(priority, new ThrowingCalls(isLinux), new ILogger(logger));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy, Is.EqualTo("the priority it started with"));
            Assert.That(logger.LogList, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Thread_SetsItsOwnPriorityBeforeTakingWork([Values] EarlyStorageApplyPriority priority)
    {
        RecordingCalls calls = new(isLinux: true);
        string[] expected = priority switch
        {
            EarlyStorageApplyPriority.Idle => ["sched_setscheduler(5)"],
            EarlyStorageApplyPriority.Low => ["sched_setscheduler(0)", "gettid", "setpriority(4242, 10)"],
            _ => ["sched_setscheduler(0)", "gettid", "setpriority(4242, 0)"]
        };

        IdleStorageApplier applier = new(LimboLogs.Instance, priority, calls);

        Assert.That(() => applier.IsParked, Is.True.After(5000, 10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(applier.Priority, Is.EqualTo(priority));
            Assert.That(calls.Calls, Is.EqualTo(expected));
            Assert.That(calls.Threads, Is.All.EqualTo("Storage early apply"));
        }
    }

    [Test]
    [Explicit("Starts a real thread and reads its kernel scheduling: Linux only, and Normal needs the right to raise a niced process back to nice 0.")]
    public void Thread_OnLinux_GetsTheKernelPolicyOfItsPriority([Values] EarlyStorageApplyPriority priority)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Reads /proc.");
        // SCHED_IDLE leaves the nice value inherited from the creating thread.
        (int Policy, int? Nice) expected = priority switch
        {
            EarlyStorageApplyPriority.Idle => (5, null),
            EarlyStorageApplyPriority.Low => (0, 10),
            _ => (0, 0)
        };
        HashSet<int> before = ApplierThreads();

        IdleStorageApplier applier = new(LimboLogs.Instance, priority, ThreadSchedulingCalls.Instance);

        Assert.That(() => applier.IsParked, Is.True.After(5000, 10));
        HashSet<int> started = ApplierThreads();
        started.ExceptWith(before);
        Assert.That(started, Has.Count.EqualTo(1));
        int tid = started.Single();
        (int policy, int nice) = ReadPolicyAndNice(tid);
        TestContext.Out.WriteLine($"{priority}: pid {Environment.ProcessId} tid {tid} policy {policy} nice {nice}");
        foreach (string line in File.ReadLines($"/proc/self/task/{tid}/sched").Where(static l => l.StartsWith("policy") || l.StartsWith("prio")))
        {
            TestContext.Out.WriteLine($"  {line}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(policy, Is.EqualTo(expected.Policy));
            if (expected.Nice is { } expectedNice) Assert.That(nice, Is.EqualTo(expectedNice));
        }
    }

    private static HashSet<int> ApplierThreads() =>
    [
        .. Directory.EnumerateDirectories("/proc/self/task")
            .Select(static dir => int.Parse(Path.GetFileName(dir)))
            .Where(IsApplierThread)
    ];

    private static bool IsApplierThread(int tid)
    {
        try
        {
            return File.ReadAllText($"/proc/self/task/{tid}/comm").StartsWith("Storage early");
        }
        // The thread ended after the listing.
        catch (IOException)
        {
            return false;
        }
    }

    // Fields 19 (nice) and 41 (policy) of /proc/<pid>/task/<tid>/stat, counted past the parenthesised name.
    private static (int Policy, int Nice) ReadPolicyAndNice(int tid)
    {
        string stat = File.ReadAllText($"/proc/self/task/{tid}/stat");
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return (int.Parse(fields[41 - 3]), int.Parse(fields[19 - 3]));
    }

    private sealed class RecordingCalls(bool isLinux, int policyErrno = 0, int niceErrno = 0, int kernelTid = Tid) : IThreadSchedulingCalls
    {
        private readonly Lock _lock = new();
        private readonly List<string> _calls = [];
        private readonly List<string?> _threads = [];

        public string[] Calls
        {
            get
            {
                lock (_lock) return [.. _calls];
            }
        }

        public string?[] Threads
        {
            get
            {
                lock (_lock) return [.. _threads];
            }
        }

        public bool IsLinux => isLinux;

        public bool TrySetSchedulerPolicy(int policy, out int errno)
        {
            Record($"sched_setscheduler({policy})");
            errno = policyErrno;
            return policyErrno == 0;
        }

        public int GetThreadId()
        {
            Record("gettid");
            return kernelTid;
        }

        public bool TrySetThreadNice(int tid, int nice, out int errno)
        {
            Record($"setpriority({tid}, {nice})");
            errno = niceErrno;
            return niceErrno == 0;
        }

        public void SetManagedPriority(ThreadPriority priority) => Record($"ThreadPriority.{priority}");

        private void Record(string call)
        {
            lock (_lock)
            {
                _calls.Add(call);
                _threads.Add(Thread.CurrentThread.Name);
            }
        }
    }

    private sealed class ThrowingCalls(bool isLinux) : IThreadSchedulingCalls
    {
        public bool IsLinux => isLinux;

        public bool TrySetSchedulerPolicy(int policy, out int errno) => throw new DllNotFoundException("libc");

        public int GetThreadId() => throw new DllNotFoundException("libc");

        public bool TrySetThreadNice(int tid, int nice, out int errno) => throw new DllNotFoundException("libc");

        public void SetManagedPriority(ThreadPriority priority) => throw new PlatformNotSupportedException();
    }
}
