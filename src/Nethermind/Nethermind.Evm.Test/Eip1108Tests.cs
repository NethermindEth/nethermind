// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Test;

public class Eip1108Tests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => (ulong)((long)MainnetSpecProvider.IstanbulBlockNumber + _blockNumberAdjustment);

    private long _blockNumberAdjustment;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();

        _blockNumberAdjustment = 0;
    }

    [TestCase(-1L, 500UL, Description = "Before Istanbul")]
    [TestCase(0L, 150UL, Description = "After Istanbul")]
    public void Test_add(long blockAdjustment, ulong expectedPrecompileGas)
    {
        _blockNumberAdjustment = blockAdjustment;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254AddPrecompile.Address, 1000L, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + expectedPrecompileGas);
    }

    [TestCase(-1L, 50000L, 40000UL, Description = "Before Istanbul")]
    [TestCase(0L, 10000L, 6000UL, Description = "After Istanbul")]
    public void Test_mul(long blockAdjustment, long gasLimit, ulong expectedPrecompileGas)
    {
        _blockNumberAdjustment = blockAdjustment;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254MulPrecompile.Address, gasLimit, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + expectedPrecompileGas);
    }

    [TestCase(-1L, 180000UL, Description = "Before Istanbul")]
    [TestCase(0L, 79000UL, Description = "After Istanbul")]
    public void Test_pairing(long blockAdjustment, ulong expectedPrecompileGas)
    {
        _blockNumberAdjustment = blockAdjustment;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254PairingCheckPrecompile.Address, 200000L, new byte[192])
            .Done;
        TestAllTracerWithOutput result = Execute(BlockNumber, 1000000L, code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + expectedPrecompileGas);
    }
}
