// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Test;
using Nethermind.Core.Threading;
using Nethermind.Logging;
using NUnit.Framework;

#nullable enable

namespace Nethermind.Consensus.Test.Processing;

[TestFixture]
public class ExpbPriorityProbeTests
{
    [Test]
    public void Off_does_not_call_native_api()
    {
        FakeNative native = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Off, native, new ILogger(new TestLogger()));

        using (probe.Enter())
        {
        }

        Assert.That(native.CallCount, Is.Zero);
    }

    [Test]
    public void Observe_records_state_without_changing_nice()
    {
        FakeNative native = new();
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Observe, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.Empty);
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("mode=observe"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Nice_restores_original_value_on_the_same_thread([Values(-1, 0, 3)] int initialNice)
    {
        FakeNative native = new() { InitialNice = initialNice };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5, initialNice }));
            Assert.That(native.CurrentNice, Is.EqualTo(initialNice));
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("mode=nice"));
            Assert.That(logger.LogList[0], Does.Contain("nice_during=-5"));
            Assert.That(logger.LogList[0], Does.Contain($"nice_after={initialNice}"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Nice_failure_is_logged_and_restore_is_attempted()
    {
        FakeNative native = new() { FailApply = true };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5, 0 }));
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
            Assert.That(logger.LogList[0], Does.Contain("setpriority_apply"));
        }
    }

    [Test]
    public void Restore_failure_is_logged_as_unsuccessful()
    {
        FakeNative native = new() { FailRestore = true };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5, 0 }));
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
            Assert.That(logger.LogList[0], Does.Contain("setpriority_restore"));
        }
    }

    [Test]
    public void Apply_readback_mismatch_is_logged_as_unsuccessful()
    {
        FakeNative native = new() { MismatchApplyReadback = true };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5, 0 }));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
            Assert.That(logger.LogList[0], Does.Contain("setpriority_apply_readback"));
        }
    }

    [Test]
    public void Later_failure_is_logged_after_a_success_for_the_same_thread()
    {
        FakeNative native = new() { FailPolicyAfterCall = 3 };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Observe, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.Exactly(2).Items);
            Assert.That(logger.LogList[1], Does.Contain("success=false"));
            Assert.That(logger.LogList[1], Does.Contain("sched_getscheduler_before"));
        }
    }

    [Test]
    public void Restore_happens_when_processing_scope_throws()
    {
        FakeNative native = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(new TestLogger()));

        Assert.That(() =>
        {
            using ExpbPriorityProbe.Scope scope = probe.Enter();
            using ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority();
            scope.CaptureDuring();
            throw new InvalidOperationException();
        }, Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5, 0 }));
            Assert.That(native.CurrentNice, Is.Zero);
        }
    }

    [Test]
    public void Native_thread_change_is_reported_without_restoring_on_another_thread()
    {
        FakeNative native = new() { ThreadIdAfterEnter = 43 };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5 }));
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("native_thread_changed"));
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
        }
    }

    [TestCase(null, 0)]
    [TestCase("off", 0)]
    [TestCase("observe", 1)]
    [TestCase("nice", 2)]
    public void Parse_mode(string? rawMode, int expected)
        => Assert.That(ExpbPriorityProbe.ParseMode(rawMode), Is.EqualTo((ExpbPriorityMode)expected));

    [Test]
    public void Parse_mode_rejects_unknown_value()
        => Assert.That(() => ExpbPriorityProbe.ParseMode("invalid"), Throws.InvalidOperationException);

    [Test]
    public void Linux_native_observe_records_real_thread_state()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("The EXPB priority probe uses Linux scheduling APIs.");
        }

        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Observe, LinuxExpbPriorityNative.Instance, new ILogger(logger));
        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("EXPB_PRIORITY mode=observe"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Linux_native_nice_apply_and_restore()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NETHERMIND_EXPB_RUN_NICE_TEST") != "1")
        {
            Assert.Ignore("Set NETHERMIND_EXPB_RUN_NICE_TEST=1 on Linux to run the privileged nice probe.");
        }

        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, LinuxExpbPriorityNative.Instance, new ILogger(logger));
        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.One.Items);
            TestContext.Progress.WriteLine(logger.LogList[0]);
            Assert.That(logger.LogList[0], Does.Contain("EXPB_PRIORITY mode=nice"));
            Assert.That(logger.LogList[0], Does.Contain("nice_during=-5"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    private sealed class FakeNative : IExpbPriorityNative
    {
        private int _getThreadIdCalls;

        public bool FailApply { get; init; }
        public bool FailRestore { get; init; }
        public bool MismatchApplyReadback { get; init; }
        public int FailPolicyAfterCall { get; init; }
        public int ThreadIdAfterEnter { get; init; } = 42;
        public int InitialNice { get; init; }
        public int CurrentNice { get; private set; }
        public List<int> SetNiceValues { get; } = [];
        public int CallCount { get; private set; }
        private bool _niceInitialized;
        private int _policyCallCount;

        public bool TryGetThreadId(out int threadId, out int error)
        {
            CallCount++;
            _getThreadIdCalls++;
            threadId = _getThreadIdCalls > 1 ? ThreadIdAfterEnter : 42;
            error = 0;
            return true;
        }

        public bool TryGetSchedulingPolicy(int threadId, out int policy, out int error)
        {
            CallCount++;
            _policyCallCount++;
            if (FailPolicyAfterCall > 0 && _policyCallCount > FailPolicyAfterCall)
            {
                policy = -1;
                error = 5;
                return false;
            }

            policy = 0;
            error = 0;
            return true;
        }

        public bool TryGetNice(int threadId, out int nice, out int error)
        {
            CallCount++;
            if (!_niceInitialized)
            {
                CurrentNice = InitialNice;
                _niceInitialized = true;
            }

            nice = CurrentNice;
            error = 0;
            return true;
        }

        public bool TrySetNice(int threadId, int nice, out int error)
        {
            CallCount++;
            SetNiceValues.Add(nice);
            if ((FailApply && nice == -5) || (FailRestore && nice == InitialNice))
            {
                error = 13;
                return false;
            }

            CurrentNice = MismatchApplyReadback && nice == -5 ? -4 : nice;
            error = 0;
            return true;
        }
    }
}
