// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Specs;

namespace Nethermind.Evm.Test;

public class Eip2537Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
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
        Assert.That(G1AddPrecompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_g1_add_after_prague()
    {
        Assert.That(G1AddPrecompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(G1AddPrecompile.Address, 1000L, new byte[256])
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
        Assert.That(G2AddPrecompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_g2_add_after_prague()
    {
        Assert.That(G2AddPrecompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(G2AddPrecompile.Address, 1000L, new byte[512])
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
        Assert.That(G1MSMPrecompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_g1_msm_after_prague()
    {
        Assert.That(G1MSMPrecompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(G1MSMPrecompile.Address, 100000L, new byte[160])
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
        Assert.That(G2MSMPrecompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_g2_msm_after_prague()
    {
        Assert.That(G2MSMPrecompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(G2MSMPrecompile.Address, 100000L, new byte[288])
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
        Assert.That(PairingCheckPrecompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_pairing_check_after_prague()
    {
        Assert.That(PairingCheckPrecompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(PairingCheckPrecompile.Address, 100000L, new byte[384])
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
        Assert.That(MapFpToG1Precompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_map_fp_to_g1_after_prague()
    {
        Assert.That(MapFpToG1Precompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(MapFpToG1Precompile.Address, 10000L, new byte[64])
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
        Assert.That(MapFp2ToG2Precompile.Address.IsPrecompile(Spec), Is.False);
    }

    [Test]
    public void Test_map_fp2_to_g2_after_prague()
    {
        Assert.That(MapFp2ToG2Precompile.Address.IsPrecompile(Spec), Is.True);

        byte[] code = Prepare.EvmCode
            .CallWithInput(MapFp2ToG2Precompile.Address, 100000L, new byte[128])
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
}
