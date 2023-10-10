// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class Eip7516BlobBaseFeeTests : VirtualMachineTestsBase
{

    [TestCase(true, 0ul)]
    [TestCase(true, 100ul)]
    [TestCase(true, 20ul)]
    [TestCase(false, 20ul)]
    [TestCase(false, 0ul)]
    [TestCase(true, 0ul)]
    [TestCase(true, 100ul)]
    [TestCase(true, 20ul)]
    [TestCase(false, 20ul)]
    [TestCase(false, 0ul)]
    public void Blob_Base_fee_opcode_should_return_expected_results(bool eip7516Enabled, ulong excessBlobGas)
    {
        _processor = new TransactionProcessor(SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);
        byte[] code = Prepare.EvmCode
            .Op(Instruction.BLOBBASEFEE)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        ForkActivation activation = eip7516Enabled ? MainnetSpecProvider.CancunActivation : MainnetSpecProvider.ShanghaiActivation;
        (Block block, Transaction transaction) = PrepareTx(activation, 100000, code);
        block.Header.ExcessBlobGas = excessBlobGas;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, block.Header, tracer);

        _ = BlobGasCalculator.TryCalculateBlobGasPricePerUnit(excessBlobGas, out UInt256 expectedGasPrice);
        if (eip7516Enabled)
        {
            AssertStorage((UInt256)0, expectedGasPrice);
        }
        else
        {
            tracer.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
            AssertStorage((UInt256)0, (UInt256)0);
        }
    }
}
