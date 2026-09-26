// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Test;

public class Eip2537Tests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => (ulong)((long)MainnetSpecProvider.PragueBlockTimestamp + _timestampAdjustment);

    private long _timestampAdjustment;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();

        _timestampAdjustment = 0;
    }

    [Test]
    public void Test_g1_add_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381G1AddPrecompile.Address), Is.False);
    }

    [Test]
    public void Test_g1_add_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381G1AddPrecompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381G1AddPrecompile.Address, 1000L, new byte[256])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 23 + // PUSH
            6 * 8 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            375
        );
    }

    [Test]
    public void Test_g2_add_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381G2AddPrecompile.Address), Is.False);
    }

    [Test]
    public void Test_g2_add_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381G2AddPrecompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381G2AddPrecompile.Address, 1000L, new byte[512])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 39 + // PUSH
            6 * 16 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            600
        );
    }

    [Test]
    public void Test_g1_msm_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381G1MsmPrecompile.Address), Is.False);
    }

    [Test]
    public void Test_g1_msm_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381G1MsmPrecompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381G1MsmPrecompile.Address, 100000L, new byte[160])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 17 + // PUSH
            6 * 5 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            12000
        );
    }

    [Test]
    public void Test_g2_msm_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381G2MsmPrecompile.Address), Is.False);
    }

    [Test]
    public void Test_g2_msm_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381G2MsmPrecompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381G2MsmPrecompile.Address, 100000L, new byte[288])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 25 + // PUSH
            6 * 9 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            22500
        );
    }

    [Test]
    public void Test_pairing_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381PairingCheckPrecompile.Address), Is.False);
    }

    [Test]
    public void Test_pairing_check_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381PairingCheckPrecompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381PairingCheckPrecompile.Address, 100000L, new byte[384])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 31 + // PUSH
            6 * 12 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            37700 + 32600
        );
    }

    [Test]
    public void Test_map_fp_to_g1_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381FpToG1Precompile.Address), Is.False);
    }

    [Test]
    public void Test_map_fp_to_g1_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381FpToG1Precompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381FpToG1Precompile.Address, 10000L, new byte[64])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 11 + // PUSH
            6 * 2 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            5500
        );
    }

    [Test]
    public void Test_map_fp2_to_g2_before_prague()
    {
        _timestampAdjustment = -12;
        Assert.That(Spec.IsPrecompile(Bls12381Fp2ToG2Precompile.Address), Is.False);
    }

    [Test]
    public void Test_map_fp2_to_g2_after_prague()
    {
        Assert.That(Spec.IsPrecompile(Bls12381Fp2ToG2Precompile.Address), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(Bls12381Fp2ToG2Precompile.Address, 100000L, new byte[128])
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result,
            GasCostOf.Transaction +
            GasCostOf.VeryLow * 15 + // PUSH
            6 * 4 + // MSTORE & expand one word
            GasCostOf.CallPrecompileEip2929 +
            23800
        );
    }

    private static IEnumerable<TestCaseData> MultiItemInputs()
    {
        const string scalar = "0000000000000000000000000000000000000000000000000000000000000002";
        const int items = 4;
        yield return new TestCaseData(Bls12381PairingCheckPrecompile.Instance,
            string.Concat(Enumerable.Repeat(Bls12381PairingCheckPrecompileTests.G1Generator + Bls12381PairingCheckPrecompileTests.G2Generator, items)))
            .SetArgDisplayNames("pairing");
        yield return new TestCaseData(Bls12381G1MsmPrecompile.Instance,
            string.Concat(Enumerable.Repeat(Bls12381PairingCheckPrecompileTests.G1Generator + scalar, items)))
            .SetArgDisplayNames("g1_msm");
        yield return new TestCaseData(Bls12381G2MsmPrecompile.Instance,
            string.Concat(Enumerable.Repeat(Bls12381PairingCheckPrecompileTests.G2Generator + scalar, items)))
            .SetArgDisplayNames("g2_msm");
    }

    [TestCaseSource(nameof(MultiItemInputs))]
    [NonParallelizable]
    public void Multi_item_input_completes_while_thread_pool_is_saturated(IPrecompile precompile, string input)
    {
        byte[] data = Convert.FromHexString(input);
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        // more blockers than the pool starts at once or injects within the join timeout
        int blockers = minWorkers + 64;
        using ManualResetEventSlim release = new(false);
        using CountdownEvent released = new(blockers);
        for (int i = 0; i < blockers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                release.Wait();
                released.Signal();
            }, null);
        }

        Result<byte[]> result = default;
        Thread caller = new(() => result = precompile.Run(data, Spec)) { IsBackground = true };
        bool completed;
        try
        {
            caller.Start();
            completed = caller.Join(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            caller.Join();
            released.Wait();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.True, "precompile waited for a thread-pool worker");
            Assert.That(result.IsError, Is.False, result.Error);
        }
    }
}
