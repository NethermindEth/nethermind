// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if DEBUG
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// The DEBUG-only finalizers on <see cref="ExecutionEnvironment"/> and <see cref="VmState{TGasPolicy}"/> report
/// pooled instances that were rented and never returned.
/// </summary>
/// <remarks>
/// Both checks were previously unable to fire: <see cref="GC.SuppressFinalize"/> in Dispose is permanent per
/// object, so a recycled instance was never finalized again, and the environment's condition was inverted.
/// The recycled cases below are the ones that regress if either returns.
/// </remarks>
[NonParallelizable]
public class PooledObjectLeakDetectionTests
{
    private bool _capturedSites;

    // Every assertion keys off the renting frame, so the opt-in site capture has to be on for the fixture.
    [OneTimeSetUp]
    public void EnableSiteCapture()
    {
        _capturedSites = PooledObjectLeakDetector.CaptureSites;
        PooledObjectLeakDetector.CaptureSites = true;
    }

    [OneTimeTearDown]
    public void RestoreSiteCapture() => PooledObjectLeakDetector.CaptureSites = _capturedSites;

    private static string Probe(Action action)
    {
        // Finalize whatever earlier fixtures leaked before the window opens, so their warnings do not land in it.
        Drain();

        StringBuilder buffer = new();
        // Synchronized because Console.Error is process-wide: the finalizer thread need not be the only writer.
        TextWriter captured = TextWriter.Synchronized(new StringWriter(buffer));
        TextWriter original = Console.Error;
        Console.SetError(captured);
        try
        {
            action();
            Drain();
        }
        finally
        {
            Console.SetError(original);
        }

        // A background GC can queue finalizers after the drain above, so restore first and drain once more:
        // no writer can then be mid-append into the buffer being read.
        GC.WaitForPendingFinalizers();
        return buffer.ToString();
    }

    private static void Drain()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static ExecutionEnvironment RentEnv(Address? executingAccount = null) =>
        ExecutionEnvironment.Rent(CodeInfo.Empty, executingAccount!, TestItem.AddressB, null, 0, UInt256.Zero, default);

    // Leaks must happen in a frame that has returned: a Debug build keeps locals alive to the end of their method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakFreshEnv() => RentEnv(TestItem.AddressA);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakNullAccountEnv() => RentEnv();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakRecycledEnv()
    {
        RentEnv(TestItem.AddressA).Dispose();
        RentEnv(TestItem.AddressA);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static VmState<EthereumGasPolicy> RentState(ExecutionEnvironment env) =>
        VmState<EthereumGasPolicy>.RentTopLevel(
            EthereumGasPolicy.FromULong(1000), ExecutionType.TRANSACTION, env, new StackAccessTracker(), Snapshot.Empty);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LeakRecycledState()
    {
        // A top level env is caller-owned: VmState.Dispose only releases it when !IsTopLevel.
        using ExecutionEnvironment first = RentEnv(TestItem.AddressA);
        using ExecutionEnvironment second = RentEnv(TestItem.AddressA);
        RentState(first).Dispose();
        RentState(second);
    }

    // Dispose puts the instance in the pool's thread-static local tier, which roots it for as long as the
    // renting thread runs. The collector only reaches a disposed instance once that thread has exited, so the
    // negative cases dispose on a thread of their own; the returned reference is what proves they did.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OnDyingThread(Func<object> body)
    {
        WeakReference? tracked = null;
        Thread thread = new(() => tracked = new WeakReference(body()));
        thread.Start();
        thread.Join();
        return tracked!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposeEnv() => OnDyingThread(static () =>
    {
        ExecutionEnvironment env = RentEnv(TestItem.AddressA);
        env.Dispose();
        return env;
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposeState() => OnDyingThread(static () =>
    {
        using ExecutionEnvironment env = RentEnv(TestItem.AddressA);
        VmState<EthereumGasPolicy> state = RentState(env);
        state.Dispose();
        return state;
    });

    private static void AssertReported(Action leak, string type, string marker) =>
        Assert.That(Probe(leak), Does.Contain($"{type} was not disposed").And.Contain(marker));

    private static void AssertNotReported(Func<WeakReference> disposeOnDyingThread, string type)
    {
        WeakReference tracked = null!;
        string reported = Probe(() => tracked = disposeOnDyingThread());
        using (Assert.EnterMultipleScope())
        {
            // Dispose clears the rent site, so the instance cannot be named; this is what keeps the case from
            // being vacuous — an instance the pool still roots is never finalized whatever the condition says.
            Assert.That(tracked.IsAlive, Is.False, "the disposed instance was not collected");
            Assert.That(reported, Does.Not.Contain($"{type} was not disposed"));
        }
    }

    [Test]
    public void Leaked_environment_is_reported() =>
        AssertReported(LeakFreshEnv, nameof(ExecutionEnvironment), nameof(LeakFreshEnv));

    [Test]
    public void Leaked_environment_reused_from_the_pool_is_reported() =>
        AssertReported(LeakRecycledEnv, nameof(ExecutionEnvironment), nameof(LeakRecycledEnv));

    [Test]
    public void Leaked_environment_rented_with_a_null_account_is_reported() =>
        AssertReported(LeakNullAccountEnv, nameof(ExecutionEnvironment), nameof(LeakNullAccountEnv));

    [Test]
    public void Leaked_state_reused_from_the_pool_is_reported() =>
        AssertReported(LeakRecycledState, nameof(VmState<>), nameof(LeakRecycledState));

    [Test]
    public void Collected_disposed_environment_is_not_reported() =>
        AssertNotReported(DisposeEnv, nameof(ExecutionEnvironment));

    [Test]
    public void Collected_disposed_state_is_not_reported() =>
        AssertNotReported(DisposeState, nameof(VmState<>));
}
#endif
