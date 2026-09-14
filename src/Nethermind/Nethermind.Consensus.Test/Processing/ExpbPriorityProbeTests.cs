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
    public void Nice_failure_is_logged_and_probe_is_disabled()
    {
        FakeNative native = new() { FailApply = true };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        int callCount = native.CallCount;
        using (probe.Enter())
        {
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5 }));
            Assert.That(native.CallCount, Is.EqualTo(callCount));
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
            Assert.That(logger.LogList[0], Does.Contain("setpriority_apply"));
        }
    }

    [Test]
    public void Nice_transient_failure_is_retried()
    {
        FakeNative native = new() { FailApplyOnce = true, SetNiceError = 5 };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Nice, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -5, -5, 0 }));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(logger.LogList, Has.Exactly(2).Items);
            Assert.That(logger.LogList[0], Does.Contain("errno=5"));
            Assert.That(logger.LogList[1], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Boost_prefers_maximum_priority_and_restores_original_value()
    {
        FakeNative native = new();
        TestLogger logger = RunProbe(ExpbPriorityMode.Boost, native);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -20, 0 }));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("mode=boost"));
            Assert.That(logger.LogList[0], Does.Contain("nice_during=-20"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Boost_falls_back_to_six_below_zero_when_maximum_is_denied()
    {
        FakeNative native = new() { FailBoostPrimary = true };
        TestLogger logger = RunProbe(ExpbPriorityMode.Boost, native);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -20, -6, 0 }));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("nice_during=-6"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Boost_reports_both_denied_attempts_without_throwing([Values(1, 13)] int denialError)
    {
        FakeNative native = new() { FailBoostPrimary = true, FailBoostFallback = true, SetNiceError = denialError };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Boost, native, new ILogger(logger));
        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }
        int callCount = native.CallCount;
        int logCount = logger.LogList.Count;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -20, -6 }));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("mode=boost"));
            Assert.That(logger.LogList[0], Does.Contain("nice_during=0"));
            Assert.That(logger.LogList[0], Does.Contain("setpriority_primary"));
            Assert.That(logger.LogList[0], Does.Contain("setpriority_fallback"));
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
        }

        using (probe.Enter())
        {
        }

        Assert.That(native.CallCount, Is.EqualTo(callCount));
        Assert.That(logger.LogList, Has.Count.EqualTo(logCount));
    }

    [Test]
    public void Boost_fallback_preserves_a_stronger_preexisting_priority()
    {
        FakeNative native = new() { InitialNice = -10, FailBoostPrimary = true };
        TestLogger logger = RunProbe(ExpbPriorityMode.Boost, native);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -20, -10, -10 }));
            Assert.That(native.CurrentNice, Is.EqualTo(-10));
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("nice_before=-10"));
            Assert.That(logger.LogList[0], Does.Contain("nice_during=-10"));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    [Test]
    public void Boost_fallback_readback_mismatch_is_unsuccessful()
    {
        FakeNative native = new() { FailBoostPrimary = true, MismatchBoostFallbackReadback = true };
        TestLogger logger = RunProbe(ExpbPriorityMode.Boost, native);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -20, -6, 0 }));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("setpriority_fallback_readback"));
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
        }
    }

    [Test]
    public void Boost_nested_scopes_restore_each_scope_value()
    {
        FakeNative native = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Boost, native, new ILogger(new TestLogger()));

        using (ExpbPriorityProbe.Scope outer = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            outer.CaptureDuring();
            using ExpbPriorityProbe.Scope inner = probe.Enter();
            inner.CaptureDuring();
        }

        Assert.That(native.SetNiceValues, Is.EqualTo(new[] { -20, -20, -20, 0 }));
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
    public void Restore_happens_when_processing_scope_throws([Values(2, 3)] int modeValue)
    {
        ExpbPriorityMode mode = (ExpbPriorityMode)modeValue;
        FakeNative native = new();
        ExpbPriorityProbe probe = new(mode, native, new ILogger(new TestLogger()));

        Assert.That(() =>
        {
            using ExpbPriorityProbe.Scope scope = probe.Enter();
            using ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority();
            scope.CaptureDuring();
            throw new InvalidOperationException();
        }, Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            int expectedNice = mode is ExpbPriorityMode.Nice ? -5 : -20;
            Assert.That(native.SetNiceValues, Is.EqualTo(new[] { expectedNice, 0 }));
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

    [TestCase(null, false, 0)]
    [TestCase(null, true, 0)]
    [TestCase(" ", false, 0)]
    [TestCase("\t", true, 0)]
    [TestCase("off", true, 0)]
    [TestCase("observe", false, 1)]
    [TestCase("nice", false, 2)]
    [TestCase("boost", false, 3)]
    public void Parse_mode(string? rawMode, bool isLinux, int expected)
        => Assert.That(ExpbPriorityProbe.ParseMode(rawMode, isLinux), Is.EqualTo((ExpbPriorityMode)expected));

    [Test]
    public void Parse_mode_rejects_unknown_value()
        => Assert.That(() => ExpbPriorityProbe.ParseMode("invalid"), Throws.InvalidOperationException);

    [TestCase("invalid", true)]
    [TestCase("boost", false)]
    public void From_environment_disables_invalid_or_unsupported_modes(string rawMode, bool isLinux)
    {
        TestLogger logger = new();

        ExpbPriorityProbe probe = ExpbPriorityProbe.FromEnvironment(rawMode, isLinux, new ILogger(logger));
        using (probe.Enter())
        {
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("disabled"));
        }
    }

    [Test]
    public void Missing_native_entry_point_is_logged_without_throwing()
    {
        FakeNative native = new() { ThrowOnGetThreadId = true };
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Boost, native, new ILogger(logger));

        Assert.DoesNotThrow(() =>
        {
            using ExpbPriorityProbe.Scope scope = probe.Enter();
            scope.CaptureDuring();
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.One.Items);
            Assert.That(logger.LogList[0], Does.Contain("gettid unavailable"));
            Assert.That(logger.LogList[0], Does.Contain("success=false"));
        }
    }

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

    [Test]
    public void Linux_native_boost_maximum_apply_and_restore()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("NETHERMIND_EXPB_RUN_BOOST_TEST") != "1")
        {
            Assert.Ignore("Set NETHERMIND_EXPB_RUN_BOOST_TEST=1 on Linux to run the privileged boost priority probe.");
        }

        TestLogger logger = new();
        LinuxExpbPriorityNative native = LinuxExpbPriorityNative.Instance;
        Assert.That(native.TryGetThreadId(out int threadId, out int threadIdError), Is.True, $"gettid errno={threadIdError}");
        Assert.That(native.TryGetNice(threadId, out int initialNice, out int niceError), Is.True, $"getpriority errno={niceError}");
        ExpbPriorityProbe probe = new(ExpbPriorityMode.Boost, native, new ILogger(logger));

        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.LogList, Has.One.Items);
            TestContext.Progress.WriteLine(logger.LogList[0]);
            Assert.That(logger.LogList[0], Does.Contain("EXPB_PRIORITY mode=boost"));
            Assert.That(logger.LogList[0], Does.Contain("nice_before=" + initialNice));
            Assert.That(logger.LogList[0], Does.Contain("nice_during=-20"));
            Assert.That(logger.LogList[0], Does.Contain("nice_after=" + initialNice));
            Assert.That(logger.LogList[0], Does.Contain("success=true"));
        }
    }

    private static TestLogger RunProbe(ExpbPriorityMode mode, FakeNative native)
    {
        TestLogger logger = new();
        ExpbPriorityProbe probe = new(mode, native, new ILogger(logger));
        using (ExpbPriorityProbe.Scope scope = probe.Enter())
        using (ThreadExtensions.Disposable handle = System.Threading.Thread.CurrentThread.SetHighestPriority())
        {
            scope.CaptureDuring();
        }

        return logger;
    }

    private sealed class FakeNative : IExpbPriorityNative
    {
        private int _getThreadIdCalls;

        public bool FailApply { get; init; }
        public bool FailApplyOnce { get; init; }
        public bool FailRestore { get; init; }
        public bool ThrowOnGetThreadId { get; init; }
        public bool FailBoostPrimary { get; init; }
        public bool FailBoostFallback { get; init; }
        public bool MismatchApplyReadback { get; init; }
        public bool MismatchBoostFallbackReadback { get; init; }
        public int SetNiceError { get; init; } = 13;
        public int FailPolicyAfterCall { get; init; }
        public int ThreadIdAfterEnter { get; init; } = 42;
        public int InitialNice { get; init; }
        public int CurrentNice { get; private set; }
        public List<int> SetNiceValues { get; } = [];
        public int CallCount { get; private set; }
        private bool _niceInitialized;
        private bool _applyFailed;
        private int _policyCallCount;

        public bool TryGetThreadId(out int threadId, out int error)
        {
            if (ThrowOnGetThreadId)
            {
                throw new EntryPointNotFoundException("gettid");
            }

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
            if ((FailApply && nice == -5)
                || (FailApplyOnce && !_applyFailed && nice == -5)
                || (FailBoostPrimary && nice == -20)
                || (FailBoostFallback && nice == -6)
                || (FailRestore && nice == InitialNice))
            {
                _applyFailed = true;
                error = SetNiceError;
                return false;
            }

            CurrentNice = MismatchApplyReadback && nice == -5
                || MismatchBoostFallbackReadback && nice == -6
                ? nice + 1
                : nice;
            error = 0;
            return true;
        }
    }
}
