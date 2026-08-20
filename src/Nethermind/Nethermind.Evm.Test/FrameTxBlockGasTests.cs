// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>EIP-7778 block-gas accounting for EIP-8141 frame transactions.</summary>
[TestFixture]
public class FrameTxBlockGasTests
{
    private ISpecProvider _specProvider;
    private ITransactionProcessor _processor;
    private IWorldState _state;
    private IDisposable _closer;
    private IReleaseSpec Spec => _specProvider.GenesisSpec;

    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Writer = TestItem.AddressB;
    private static readonly Address Inert = TestItem.AddressC;

    [SetUp]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance));
        _state = TestWorldStateFactory.CreateForTest();
        _closer = _state.BeginScope(IWorldState.PreGenesis);
        EthereumCodeInfoRepository codeInfoRepository = new(_state);
        EthereumVirtualMachine vm = new(new TestBlockhashProvider(_specProvider), _specProvider, LimboLogs.Instance);
        _processor = new EthereumTransactionProcessor(BlobBaseFeeCalculator.Instance, _specProvider, _state, vm, codeInfoRepository, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _closer?.Dispose();

    [Test]
    public void Execute_PayloadFrameWritesFreshSlot_ReportsTheChargeInTheStateDimension()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        Deploy(Inert, Prepare.EvmCode.Op(Instruction.STOP).Done);

        TestAllTracerWithOutput writing = new();
        TestAllTracerWithOutput inert = new();
        Assert.That(Process(FrameTx(nonce: 0, Writer), writing).TransactionExecuted, Is.True);
        Assert.That(Process(FrameTx(nonce: 1, Inert), inert).TransactionExecuted, Is.True);

        const ulong stateCharge = (ulong)GasCostOf.SSetState;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inert.GasConsumedResult.BlockStateGas, Is.Zero,
                "a frame that creates no state owes no state gas");
            Assert.That(writing.GasConsumedResult.BlockStateGas, Is.EqualTo(stateCharge),
                "the fresh slot's state-growth charge belongs to the state dimension");
            Assert.That(writing.GasConsumedResult.EffectiveBlockGas,
                Is.EqualTo(writing.GasConsumedResult.SpentGas - stateCharge),
                "the state charge leaves the regular dimension; counting it in both bills the block twice");
        }
    }

    [Test]
    public void Execute_PayloadFrameReverts_OwesNoStateGas()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        TestAllTracerWithOutput tracer = new();
        Assert.That(Process(FrameTx(nonce: 0, Writer), tracer).TransactionExecuted, Is.True);

        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
            "a reverted frame commits no state, so it grows none");
    }

    /// <summary>An atomic batch whose later frame fails gives back the state gas its earlier frame owed.</summary>
    /// <remarks>The unroll restores the pre-batch state, so the slot the first frame wrote never reaches
    /// the block and must not be priced into the block's state dimension.</remarks>
    [Test]
    public void Execute_AtomicBatchUnrolls_GivesBackTheStateGasOfTheRolledBackFrames()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        Deploy(Inert, Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done);

        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, TxFrame.AtomicBatchFlag, Writer, gasLimit: 400_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Inert, gasLimit: 400_000, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
            "the batch was rolled back, so the slot its first frame wrote never grew the state");
    }

    private static byte[] ApproveCode(byte scope) =>
        Prepare.EvmCode.PushData(scope).PushData(0).PushData(0).Op(Instruction.APPROVE).Done;

    private void Deploy(Address address, byte[] code, UInt256 balance = default)
    {
        _state.CreateAccount(address, balance);
        _state.InsertCode(address, code, Spec);
        _state.Commit(Spec);
        _state.CommitTree(0);
    }

    private static Transaction FrameTx(ulong nonce, Address target) =>
        FrameTx(nonce,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, target, gasLimit: 400_000, UInt256.Zero, default));

    private static Transaction FrameTx(ulong nonce, params TxFrame[] frames) =>
        new()
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = nonce,
            SenderAddress = Sender,
            Frames = frames,
            FrameSignatures = [],
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    private TransactionResult Process(Transaction tx, ITxTracer tracer)
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithBaseFeePerGas(0)
            .WithTransactions(tx)
            .WithGasLimit(30_000_000).TestObject;
        return _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);
    }
}
