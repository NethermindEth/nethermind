// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Evm.Precompiles.Bls;

namespace Nethermind.Evm.Test;

public class Eip2537Tests : VirtualMachineTestsBase
{
    protected override ulong Timestamp => (ulong)((long)MainnetSpecProvider.PragueBlockTimestamp + _timestampAdjustment);

    private long _timestampAdjustment;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();

        _timestampAdjustment = 0;
    }

    [Test]
    public void Test_add_before_prague()
    {
        _timestampAdjustment = -12;
        byte[] code = Prepare.EvmCode
            .CallWithInput(G1AddPrecompile.Address, 1000L, new byte[256])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150);
    }

    [Test]
    public void Test_add_after_prague()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(G1AddPrecompile.Address, 1000L, new byte[256])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 375);
    }

    [Test]
    public void Test_msm_before_prague()
    {
        _timestampAdjustment = -12;
        byte[] code = Prepare.EvmCode
            .CallWithInput(G1MSMPrecompile.Address, 50000L, new byte[160])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 40000L);
    }

    [Test]
    public void Test_msm_after_prague()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(G1MSMPrecompile.Address, 10000L, new byte[160])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 6000L);
    }

    [Test]
    public void Test_pairing_before_prague()
    {
        _timestampAdjustment = -12;
        byte[] code = Prepare.EvmCode
            .CallWithInput(PairingCheckPrecompile.Address, 200000L, new byte[384])
            .Done;
        TestAllTracerWithOutput result = Execute(BlockNumber, 1000000L, code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 100000L + 80000L);
    }

    [Test]
    public void Test_pairing_after_prague()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(PairingCheckPrecompile.Address, 200000L, new byte[284])
            .Done;
        TestAllTracerWithOutput result = Execute(BlockNumber, 1000000L, code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 45000L + 34000L);
    }
}
