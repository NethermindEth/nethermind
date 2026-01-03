// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Specs;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip152Tests : VirtualMachineTestsBase
{
    private const int InputLength = 213;
    private ulong _blockNumber = MainnetSpecProvider.IstanbulBlockNumber;
    protected override ulong BlockNumber => _blockNumber;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();
        _blockNumber = MainnetSpecProvider.IstanbulBlockNumber;
    }

    [Test]
    public void before_istanbul()
    {
        _blockNumber = MainnetSpecProvider.IstanbulBlockNumber - 1UL;
        Address precompileAddress = Blake2FPrecompile.Address;
        Assert.That(Spec.IsPrecompile(precompileAddress), Is.False);
    }

    [Test]
    public void after_istanbul()
    {
        byte[] code = Prepare.EvmCode
            .CallWithInput(Blake2FPrecompile.Address, 1000L, new byte[InputLength])
            .Done;
        TestAllTracerWithOutput result = Execute(code);
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
    }
}
