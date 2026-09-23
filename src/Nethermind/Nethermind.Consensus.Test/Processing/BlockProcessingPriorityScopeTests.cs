// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[TestFixture]
public class BlockProcessingPriorityScopeTests
{
    private static IEnumerable<TestCaseData> NativePriorityCases()
    {
        yield return new TestCaseData(0, false, -20, new[] { -20, 0 });
        yield return new TestCaseData(0, true, -6, new[] { -20, -6, 0 });
        yield return new TestCaseData(-1, true, -6, new[] { -20, -6, -1 });
        yield return new TestCaseData(-10, true, -10, new[] { -20, -10 });
    }

    [TestCaseSource(nameof(NativePriorityCases))]
    public void Native_priority_uses_best_available_value_and_restores_original(
        int initialNice,
        bool failPrimary,
        int expectedDuring,
        int[] expectedSetValues)
    {
        FakeNative native = new() { InitialNice = initialNice, FailPrimary = failPrimary };

        using (BlockProcessingPriorityScope scope = Enter(native))
        {
            Assert.That(native.CurrentNice, Is.EqualTo(expectedDuring));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetValues, Is.EqualTo(expectedSetValues));
            Assert.That(native.CurrentNice, Is.EqualTo(initialNice));
        }
    }

    [Test]
    public void Denied_native_priority_does_not_fail_processing()
    {
        FakeNative native = new() { FailPrimary = true, FailFallback = true };
        TestLogger logger = new();

        Assert.DoesNotThrow(() =>
        {
            using BlockProcessingPriorityScope scope = Enter(native, logger);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(native.SetValues, Is.EqualTo(new[] { -20, -6 }));
            Assert.That(logger.LogList, Has.One.Contains("setpriority"));
        }
    }

    [Test]
    public void Native_priority_is_restored_when_the_processing_scope_throws()
    {
        FakeNative native = new();
        ThreadPriority originalPriority = Thread.CurrentThread.Priority;

        Assert.That(() =>
        {
            using BlockProcessingPriorityScope scope = Enter(native);
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(ThreadPriority.Highest));
            throw new InvalidOperationException();
        }, Throws.TypeOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(originalPriority));
            Assert.That(native.CurrentNice, Is.Zero);
            Assert.That(native.SetValues, Is.EqualTo(new[] { -20, 0 }));
        }
    }

    [Test]
    public void Native_read_failure_keeps_managed_priority_and_does_not_throw()
    {
        FakeNative native = new() { FailGet = true };
        TestLogger logger = new();
        ThreadPriority originalPriority = Thread.CurrentThread.Priority;

        using (BlockProcessingPriorityScope scope = Enter(native, logger))
        {
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(ThreadPriority.Highest));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(originalPriority));
            Assert.That(native.SetValues, Is.Empty);
            Assert.That(logger.LogList, Has.One.Contains("getpriority"));
        }
    }

    [Test]
    public void Disabled_native_priority_keeps_managed_priority_only()
    {
        FakeNative native = new() { InitialNice = 5 };
        ThreadPriority originalPriority = Thread.CurrentThread.Priority;

        using (BlockProcessingPriorityScope scope = Enter(native, boostNativePriority: false))
        {
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(ThreadPriority.Highest));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(originalPriority));
            Assert.That(native.SetValues, Is.Empty);
        }
    }

    [Test]
    public void Native_restore_failure_is_logged_without_replacing_processing_failure()
    {
        FakeNative native = new() { FailRestore = true };
        TestLogger logger = new();

        Assert.That(() =>
        {
            using BlockProcessingPriorityScope scope = Enter(native, logger);
            throw new InvalidOperationException();
        }, Throws.TypeOf<InvalidOperationException>());

        Assert.That(logger.LogList, Has.One.Contains("restore native"));
    }

    [Test]
    public void Linux_native_priority_is_restored_on_a_dedicated_thread([Values(0, -1)] int requestedInitialNice)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Native nice priority is a Linux feature.");
        }

        Exception? failure = null;
        int originalNice = 0;
        int expectedDuring = 0;
        int duringNice = 0;
        int afterNice = 0;
        bool setupDenied = false;
        Thread thread = new(() =>
        {
            try
            {
                if (!TrySetNativeNice(requestedInitialNice))
                {
                    setupDenied = true;
                    return;
                }

                originalNice = ReadNativeNice();
                int fallbackNice = Math.Min(originalNice, -6);
                if (TrySetNativeNice(-20))
                {
                    expectedDuring = -20;
                    Assert.That(TrySetNativeNice(originalNice), Is.True);
                }
                else if (TrySetNativeNice(fallbackNice))
                {
                    expectedDuring = fallbackNice;
                    Assert.That(TrySetNativeNice(originalNice), Is.True);
                }
                else
                {
                    expectedDuring = originalNice;
                }

                using (BlockProcessingPriorityScope scope = BlockProcessingPriorityScope.Enter(new ILogger(new TestLogger()), boostNativePriority: true))
                {
                    duringNice = ReadNativeNice();
                }

                afterNice = ReadNativeNice();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });

        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "Native priority test thread did not complete.");
        if (setupDenied)
        {
            if (requestedInitialNice == -1)
            {
                Assert.Ignore("The test process cannot set native nice=-1.");
            }

            Assert.Fail($"The test process cannot set native nice={requestedInitialNice}.");
        }

        Assert.That(failure, Is.Null, failure?.ToString());
        TestContext.Progress.WriteLine($"initial={originalNice} expected={expectedDuring} during={duringNice} after={afterNice}");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(duringNice, Is.EqualTo(expectedDuring));
            Assert.That(afterNice, Is.EqualTo(originalNice));
        }
    }

    [Test]
    public void Non_linux_path_keeps_managed_priority_only()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Ignore("The managed-only path is covered when this test runs on Windows.");
        }

        ThreadPriority originalPriority = Thread.CurrentThread.Priority;
        using (BlockProcessingPriorityScope scope = BlockProcessingPriorityScope.Enter(new ILogger(new TestLogger()), boostNativePriority: true))
        {
            Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(ThreadPriority.Highest));
        }

        Assert.That(Thread.CurrentThread.Priority, Is.EqualTo(originalPriority));
    }

    private static BlockProcessingPriorityScope Enter(FakeNative native, TestLogger? logger = null, bool boostNativePriority = true)
    {
        logger ??= new TestLogger();
        return BlockProcessingPriorityScope.Enter(
            new ILogger(logger),
            boostNativePriority,
            native.TryGetNice,
            native.TrySetNice);
    }

    private sealed class FakeNative
    {
        public int InitialNice { get; init; }
        public bool FailPrimary { get; init; }
        public bool FailFallback { get; init; }
        public bool FailGet { get; init; }
        public bool FailRestore { get; init; }
        public int CurrentNice { get; private set; }
        public List<int> SetValues { get; } = [];

        public bool TryGetNice(out int nice, out int error)
        {
            if (FailGet)
            {
                nice = 0;
                error = 5;
                return false;
            }

            nice = CurrentNice == 0 && SetValues.Count == 0 ? InitialNice : CurrentNice;
            CurrentNice = nice;
            error = 0;
            return true;
        }

        public bool TrySetNice(int nice, out int error)
        {
            SetValues.Add(nice);
            if ((nice == -20 && FailPrimary)
                || (nice == -6 && FailFallback)
                || (nice == InitialNice && FailRestore))
            {
                error = 13;
                return false;
            }

            CurrentNice = nice;
            error = 0;
            return true;
        }
    }

    private static int ReadNativeNice()
    {
        Marshal.SetLastPInvokeError(0);
        int nice = getpriority(0, 0);
        int error = nice == -1 ? Marshal.GetLastPInvokeError() : 0;
        if (nice == -1 && error != 0)
        {
            throw new InvalidOperationException($"getpriority failed with errno={error}");
        }

        return nice;
    }

    private static bool TrySetNativeNice(int nice)
    {
        int result = setpriority(0, 0, nice);
        return result == 0;
    }

    [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int getpriority(int which, int who);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int setpriority(int which, int who, int priority);
}
