// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs;
using NUnit.Framework;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Test;

public class Eip1108Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber + _blockNumberAdjustment;

    private int _blockNumberAdjustment;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();

        _blockNumberAdjustment = 0;
    }

    [TestCase(-1, 500L, Description = "Before Istanbul")]
    [TestCase(0, 150L, Description = "After Istanbul")]
    public void Test_add(int blockAdjustment, long expectedPrecompileGas)
    {
        _blockNumberAdjustment = blockAdjustment;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254AddPrecompile.Address, 1000L, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + expectedPrecompileGas);
    }

    [TestCase(-1, 50000L, 40000L, Description = "Before Istanbul")]
    [TestCase(0, 10000L, 6000L, Description = "After Istanbul")]
    public void Test_mul(int blockAdjustment, long gasLimit, long expectedPrecompileGas)
    {
        _blockNumberAdjustment = blockAdjustment;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254MulPrecompile.Address, gasLimit, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + expectedPrecompileGas);
    }

    [TestCase(-1, 180000L, Description = "Before Istanbul")]
    [TestCase(0, 79000L, Description = "After Istanbul")]
    public void Test_pairing(int blockAdjustment, long expectedPrecompileGas)
    {
        _blockNumberAdjustment = blockAdjustment;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254PairingPrecompile.Address, 200000L, new byte[192])
            .Done;
        TestAllTracerWithOutput result = Execute(BlockNumber, 1000000L, code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + expectedPrecompileGas);
    }
}
