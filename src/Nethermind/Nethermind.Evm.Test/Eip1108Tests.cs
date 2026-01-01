// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs;
using Nethermind.Core.Specs;
using NUnit.Framework;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Evm.Test;

public class Eip1108Tests : VirtualMachineTestsBase
{
    private ulong _blockNumber = MainnetSpecProvider.IstanbulBlockNumber;
    protected override ulong BlockNumber => _blockNumber;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();
        _blockNumber = MainnetSpecProvider.IstanbulBlockNumber;
    }

    [Test]
    public void Test_add_before_istanbul()
    {
        _blockNumber = MainnetSpecProvider.IstanbulBlockNumber - 1UL;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254AddPrecompile.Address, 1000L, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 500);
    }

    [Test]
    public void Test_add_after_istanbul()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254AddPrecompile.Address, 1000L, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 150);
    }

    [Test]
    public void Test_mul_before_istanbul()
    {
        _blockNumber = MainnetSpecProvider.IstanbulBlockNumber - 1UL;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254MulPrecompile.Address, 50000L, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 40000L);
    }

    [Test]
    public void Test_mul_after_istanbul()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254MulPrecompile.Address, 10000L, new byte[128])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 4 * 12 + 7 * 3 + GasCostOf.CallEip150 + 6000L);
    }

    [Test]
    public void Test_pairing_before_istanbul()
    {
        _blockNumber = MainnetSpecProvider.IstanbulBlockNumber - 1UL;
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254PairingPrecompile.Address, 200000L, new byte[192])
            .Done;
        TestAllTracerWithOutput result = Execute(new ForkActivation(BlockNumber, Timestamp), 1_000_000UL, code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 100000L + 80000L);
    }

    [Test]
    public void Test_pairing_after_istanbul()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(BN254PairingPrecompile.Address, 200000L, new byte[192])
            .Done;
        TestAllTracerWithOutput result = Execute(new ForkActivation(BlockNumber, Timestamp), 1_000_000UL, code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        AssertGas(result, 21000 + 6 * 12 + 7 * 3 + GasCostOf.CallEip150 + 45000L + 34000L);
    }
}
