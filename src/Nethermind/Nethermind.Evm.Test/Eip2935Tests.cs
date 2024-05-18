// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class Eip2935Tests : VirtualMachineTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.PragueBlockTimestamp;


    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
    }


    [TestCase(MainnetSpecProvider.CancunBlockTimestamp, false)]
    [TestCase(MainnetSpecProvider.PragueBlockTimestamp, true)]
    public void CorrectBlockhashBeingUsed(ulong timestamp, bool eipEnabled)
    {
        const long blockNumber = 256;
        byte[] bytecode =
            Prepare.EvmCode
                .PushData(blockNumber)
                .Op(Instruction.BLOCKHASH)
                .PushData(0)
                .Op(Instruction.MSTORE)
                .PushData(32)
                .PushData(0)
                .Op(Instruction.RETURN)
                .Done;


        (Block block, Transaction transaction) = PrepareTx(new ForkActivation(BlockNumber, timestamp), 100000, bytecode);
        CallOutputTracer callOutputTracer = new();
        _processor.Execute(transaction, block.Header, callOutputTracer);

        long expected = eipEnabled ? blockNumber + Eip2935Constants.RingBufferSize : blockNumber;
        callOutputTracer.ReturnValue!.Should().BeEquivalentTo(Keccak.Compute(expected.ToString()).BytesToArray());
    }
}
