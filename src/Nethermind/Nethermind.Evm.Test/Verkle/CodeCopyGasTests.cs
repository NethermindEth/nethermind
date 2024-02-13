// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Verkle;

[TestFixture]
public class CodeCopyGasTests: VerkleVirtualMachineTestsBase
{
    [Test]
    public void TestCodeCopyUpdatedGas()
    {
        var code = new byte[]
        {
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 32,
            (byte)Instruction.CODECOPY
        };
        TestAllTracerWithOutput receipt = Execute(code);
        ;
        Assert.That(receipt.GasSpent,
            Is.EqualTo(GasCostOf.Transaction + GasCostOf.VeryLow * 49 + GasCostOf.Memory * 3 +
                       (GasCostOf.WitnessChunkRead * (int)Math.Ceiling((double)code.Length / 31))), "gas");
    }

    [Test]
    public void TestNoChargeForCallDataCodeCopy()
    {
        TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether());
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);

        var code = new byte[]
        {
            (byte)Instruction.PUSH1,
            32, // length
            (byte)Instruction.PUSH1,
            0, // src
            (byte)Instruction.PUSH1,
            32, // dest
            (byte)Instruction.CODECOPY
        };
        Transaction createTx = Build.A.Transaction.WithCode(code).WithValue(0.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(createTx).WithGasLimit(2 * 1000000).TestObject;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(createTx, block.Header, tracer);

        const long intrinsicGas = GasCostOf.Transaction + GasCostOf.Create + 102;
        const long contractCreateInitCost = GasCostOf.WitnessBranchRead
                                        + GasCostOf.WitnessBranchWrite
                                        + (GasCostOf.WitnessChunkRead + GasCostOf.WitnessChunkWrite) * 3;
        const long additionalGasForContractCreated = (GasCostOf.WitnessChunkRead + GasCostOf.WitnessChunkWrite) * 2;

        Assert.That(tracer.GasSpent, Is.EqualTo(intrinsicGas + contractCreateInitCost + additionalGasForContractCreated + GasCostOf.VeryLow * 4 + GasCostOf.Memory * 3));
    }

    [Test]
    public void TestNoChargeForCallDataCodeCopy2()
    {
        TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether());
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);

        var code = new byte[]
        {
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 1, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 32,
            (byte)Instruction.CODECOPY
        };

        Transaction createTx = Build.A.Transaction.WithCode(code).WithValue(0.Ether()).WithGasLimit(1000000).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
        Block block = Build.A.Block.WithNumber(BlockNumber)
            .WithTimestamp(Timestamp)
            .WithTransactions(createTx).WithGasLimit(2 * 1000000).TestObject;

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(createTx, block.Header, tracer);

        const long intrinsicGas = GasCostOf.Transaction + GasCostOf.Create + 1126;
        const long contractCreateInitCost = GasCostOf.WitnessBranchRead
                                            + GasCostOf.WitnessBranchWrite
                                            + (GasCostOf.WitnessChunkRead + GasCostOf.WitnessChunkWrite) * 3;
        const long additionalGasForContractCreated = (GasCostOf.WitnessChunkRead + GasCostOf.WitnessChunkWrite) * 2;

        Assert.That(tracer.GasSpent, Is.EqualTo(intrinsicGas + contractCreateInitCost + additionalGasForContractCreated + GasCostOf.VeryLow * 49 + GasCostOf.Memory * 3));
    }
}
