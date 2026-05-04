// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Test;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture]
public class XdcOpcodesTests : VirtualMachineTestsBase
{
    private TestableXdcBlockProcessor _xdcProcessor = null!;

    [SetUp]
    public new void Setup()
    {
        base.Setup();
        _xdcProcessor = new TestableXdcBlockProcessor();
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, Machine, CodeInfoRepository, LimboLogs.Instance);
    }

    // In XDC, ExcessBlobGas is always null — blob transactions are never used.
    // BLOBBASEFEE must return 0 when enabled, and BadInstruction when disabled.
    [TestCase(true)]
    [TestCase(false)]
    public void BlobBaseFee_opcode_returns_zero_when_enabled_and_bad_instruction_when_disabled(bool eip4844Enabled)
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.BLOBBASEFEE)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx((0, 0), 100000, code);
        block.Header.ExcessBlobGas = null;

        IReleaseSpec spec = new XdcReleaseSpec { IsEip4844Enabled = eip4844Enabled };
        BlockExecutionContext ctx = _xdcProcessor.CreateBlockExecutionContext(block.Header, spec);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, ctx, tracer);

        if (eip4844Enabled)
        {
            tracer.Error.Should().BeNull();
            AssertStorage(UInt256.Zero, UInt256.Zero);
        }
        else
        {
            tracer.Error.Should().Be(EvmExceptionType.BadInstruction.ToString());
        }
    }

    // In XDC, PREVRANDAO returns keccak(blockNumber) instead of the header Random field,
    // matching Go-XDC's big.Int.Bytes() behavior (block 0 produces empty bytes → keccak of empty).
    [
        TestCase(0L, "0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470"),
        TestCase(1L, "0x5fe7f977e71dba2ea1a68e21057beebb9be2ac30c6410aa38d4f3fbe41dcffd2"),
        TestCase(100L, "0xf1918e8562236eb17adc8502332f4c9c82bc14e19bfc0aa10ab674ff75b3d2f3"),
    ]
    public void PrevRandao_opcode_returns_keccak_of_block_number(long blockNumber, string expectedPrevRandaoHex)
    {
        byte[] code = Prepare.EvmCode
            .Op(Instruction.PREVRANDAO)
            .PushData(0)
            .Op(Instruction.SSTORE)
            .Done;

        (Block block, Transaction transaction) = PrepareTx((blockNumber, 0), 100000, code);

        IReleaseSpec spec = new XdcReleaseSpec { IsEip4844Enabled = true };
        BlockExecutionContext ctx = _xdcProcessor.CreateBlockExecutionContext(block.Header, spec);

        TestAllTracerWithOutput tracer = CreateTracer();
        _processor.Execute(transaction, ctx, tracer);

        tracer.Error.Should().BeNull();
        AssertStorage(UInt256.Zero, new Hash256(expectedPrevRandaoHex));
    }

    private class TestableXdcBlockProcessor : XdcBlockProcessor
    {
        public TestableXdcBlockProcessor() : base(
            Substitute.For<ISpecProvider>(),
            Substitute.For<IBlockValidator>(),
            Substitute.For<IRewardCalculator>(),
            Substitute.For<IBlockProcessor.IBlockTransactionsExecutor>(),
            Substitute.For<IWorldState>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IBeaconBlockRootHandler>(),
            Substitute.For<IBlockhashStore>(),
            NullLogManager.Instance,
            Substitute.For<IWithdrawalProcessor>(),
            Substitute.For<IExecutionRequestsProcessor>(),
            Substitute.For<IBlockAccessListManager>())
        { }

        public new BlockExecutionContext CreateBlockExecutionContext(BlockHeader header, IReleaseSpec spec)
            => base.CreateBlockExecutionContext(header, spec);
    }
}
