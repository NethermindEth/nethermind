// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if DEBUG
using System;
using System.IO;
using System.Runtime.CompilerServices;
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
    // Warnings from other fixtures can land in the capture window, so every assertion keys off the frame of
    // the helper that rented the instance under test rather than the buffer being empty.
    private static string Probe(Action action, [CallerMemberName] string caller = "")
    {
        TextWriter original = Console.Error;
        StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            action();
            for (int i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            Console.SetError(original);
        }

        return captured.ToString();
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
    private static void DisposeEnv() => RentEnv(TestItem.AddressA).Dispose();

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DisposeState()
    {
        using ExecutionEnvironment env = RentEnv(TestItem.AddressA);
        RentState(env).Dispose();
    }

    private static void AssertReported(Action leak, string type, string marker) =>
        Assert.That(Probe(leak), Does.Contain($"{type} was not disposed").And.Contain(marker));

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
        AssertReported(LeakRecycledState, "VmState", nameof(LeakRecycledState));

    [Test]
    public void Disposed_environment_is_not_reported() =>
        Assert.That(Probe(DisposeEnv), Does.Not.Contain(nameof(DisposeEnv)));

    [Test]
    public void Disposed_state_is_not_reported() =>
        Assert.That(Probe(DisposeState), Does.Not.Contain(nameof(DisposeState)));
}
#endif
