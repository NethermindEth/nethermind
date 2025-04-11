// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip2537Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp + (ulong)_timestampAdjustment;

    private int _timestampAdjustment;

    [TearDown]
    public override void TearDown()
    {
        base.TearDown();

        _timestampAdjustment = 0;
    }

    [Test]
    public void G1Add_precompile_invalid_input_gas_consumption()
    {
        _timestampAdjustment = 0;

        byte[] code = Prepare.EvmCode
            .CallWithInput(G1AddPrecompile.Address, 100000L, Bytes.FromHexString("0000000000000000000000000000000012196c5a43d69224d8713389285f26b98f86ee910ab3dd668e413738282003cc5b7357af9a7af54bb713d62255e80f560000000000000000000000000000000006ba8102bfbeea4416b710c73e8cce3032c31c6269c44906f8ac4f7874ce99fb17559992486528963884ce429a992fee000000000000000000000000000000000001101098f5c39893765766af4512a0c74e1bb89bc7e6fdf14e3e7337d257cc0f94658179d83320b99f31ff94cd2bac0000000000000000000000000000000003e1a9f9f44ca2cdab4f43a1a3ee3470fdf90b2fc228eb3b709fcd72f014838ac82a6d797aeefed9a0804b22ed1ce8f0"))
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(21592));
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        }
    }

    [Test]
    public void G1Add_precompile_valid_input_gas_consumption()
    {
        _timestampAdjustment = 0;

        byte[] code = Prepare.EvmCode
            .CallWithInput(G1AddPrecompile.Address, 100000L, Bytes.FromHexString("0000000000000000000000000000000012196c5a43d69224d8713389285f26b98f86ee910ab3dd668e413738282003cc5b7357af9a7af54bb713d62255e80f560000000000000000000000000000000006ba8102bfbeea4416b710c73e8cce3032c31c6269c44906f8ac4f7874ce99fb17559992486528963884ce429a992fee000000000000000000000000000000000001101098f5c39893765766af4512a0c74e1bb89bc7e6fdf14e3e7337d257cc0f94658179d83320b99f31ff94cd2bac0000000000000000000000000000000003e1a9f9f44ca2cdab4f43a1a3ee3470fdf90b2fc228eb3b709fcd72f014838ac82a6d797aeefed9a0804b22ed1ce8f7"))
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(21592));
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        }
    }

    [Test]
    public void G1Add_precompile_before_enabled_gas_consumprion()
    {
        _timestampAdjustment = -1;

        byte[] code = Prepare.EvmCode
            .CallWithInput(G1AddPrecompile.Address, 100000L, [0])
            .Done;

        TestAllTracerWithOutput receipt = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(receipt.GasSpent, Is.EqualTo(21133));
            Assert.That(receipt.StatusCode, Is.EqualTo(StatusCode.Success));
        }
    }
}
