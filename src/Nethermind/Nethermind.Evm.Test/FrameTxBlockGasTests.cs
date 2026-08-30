// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
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
    private static readonly Address Asserter = TestItem.AddressD;

    [SetUp]
    public void Setup()
    {
        // EIP-7906 is on so a POST_TX frame is admissible; the opcodes it adds are unused here.
        _specProvider = new TestSpecProvider(new OverridableReleaseSpec(Eip8141Prototype.Instance) { IsEip7906Enabled = true });
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

    /// <summary>
    /// A frame's state gas is drawn from its own <c>limits.state</c> reservoir, independent of
    /// <c>limits.execution</c>: a fresh-slot write whose execution budget cannot absorb the state charge
    /// still succeeds when the state budget covers it, and the same charge lands in the state dimension.
    /// </summary>
    [Test]
    public void Execute_PayloadFrameStateBudgetCoversTheWrite_SucceedsFromTheStateReservoir()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        const ulong executionBudget = 30_000;
        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Writer, executionBudget, 150_000, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        const ulong stateCharge = (ulong)GasCostOf.SSetState;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Get(new StorageCell(Writer, (UInt256)0)).ToArray(), Is.Not.All.EqualTo((byte)0),
                "the write committed, so its state gas came from the reservoir rather than out-of-gassing");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(stateCharge),
                "the reservoir-funded write still bills the state dimension");
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas,
                Is.EqualTo(tracer.GasConsumedResult.SpentGas - stateCharge));
        }
    }

    /// <summary>
    /// With no state budget the same write's state charge exceeds the empty state pool, so the frame halts
    /// and commits nothing: execution gas is never spent on the state charge.
    /// </summary>
    [Test]
    public void Execute_PayloadFrameStateChargeExceedsEmptyStatePool_HaltsAndOwesNoStateGas()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        const ulong executionBudget = 30_000;
        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Writer, executionBudget, 0, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Get(new StorageCell(Writer, (UInt256)0)).ToArray(), Is.All.EqualTo((byte)0),
                "the write halted out of gas, so no slot was committed");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        }
    }

    /// <summary>
    /// A fresh-slot write whose state charge exceeds a non-empty state pool halts even when the execution
    /// budget could have absorbed the deficit: the pools are independent, so execution never funds state.
    /// </summary>
    [Test]
    public void Execute_PayloadFrameStateChargeExceedsStatePool_HaltsInsteadOfSpillingIntoExecution()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        const ulong executionBudget = 200_000;
        const ulong stateBudget = 50_000;
        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Writer, executionBudget, stateBudget, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Get(new StorageCell(Writer, (UInt256)0)).ToArray(), Is.All.EqualTo((byte)0),
                "the state pool could not cover the write and execution must not fund it, so no slot was committed");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero);
        }
    }

    /// <summary>
    /// A frame whose state reservoir covers a fresh-slot write but then exceptionally halts commits nothing,
    /// so it owes zero state gas and is charged only its execution budget rather than the depleted reservoir.
    /// </summary>
    [Test]
    public void Execute_PayloadFrameConsumesStateThenHalts_OwesNoStateGas()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode
            .PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.INVALID).Done);

        const ulong executionBudget = 30_000;
        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Writer, executionBudget, 150_000, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Get(new StorageCell(Writer, (UInt256)0)).ToArray(), Is.All.EqualTo((byte)0),
                "the frame halted, so its write rolled back and committed no slot");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
                "a halted frame grows no state, so it owes none even though it drew from the reservoir");
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas,
                Is.EqualTo(tracer.GasConsumedResult.SpentGas),
                "no state charge is carved out of the regular dimension when the state gas is zero");
        }
    }

    /// <summary>
    /// When the calldata floor exceeds the execution component, the floor binds on the execution dimension
    /// alone and the frame's state gas is charged on top, so gas_used is the floor plus the state gas rather
    /// than the floor absorbing it.
    /// </summary>
    [Test]
    public void Execute_CalldataFloorBindsWithStateGas_ChargesTheStateGasOnTopOfTheFloor()
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        byte[] calldata = new byte[8192];
        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Writer, executionGasLimit: 200_000, stateGasLimit: 150_000, UInt256.Zero, calldata));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        const ulong stateCharge = (ulong)GasCostOf.SSetState;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Get(new StorageCell(Writer, (UInt256)0)).ToArray(), Is.Not.All.EqualTo((byte)0),
                "the write committed from the state reservoir");
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(stateCharge),
                "the fresh slot's state charge lands in the state dimension");
            Assert.That(tracer.GasConsumedResult.SpentGas,
                Is.EqualTo(tracer.GasConsumedResult.EffectiveBlockGas + stateCharge),
                "gas_used is the execution floor plus the state gas; the floor never absorbs the state charge");
        }
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
            new TxFrame(TxFrame.ModeSender, TxFrame.AtomicBatchFlag, Writer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Inert, gasLimit: 400_000, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.Zero,
            "the batch was rolled back, so the slot its first frame wrote never grew the state");
    }

    /// <summary>A failed EIP-7906 assertion gives back the state gas of the body it discards.</summary>
    /// <remarks>
    /// The body's writes go with the prefix snapshot the assertion restores. Leaving their charge in the
    /// state dimension inflates S, and header gasUsed = max(G - S, S) then lands below the true G.
    /// </remarks>
    [TestCase(true, 0ul, TestName = "A reverted POST_TX assertion discards the body's state gas")]
    [TestCase(false, (ulong)GasCostOf.SSetState, TestName = "A satisfied POST_TX assertion keeps the body's state gas")]
    public void Execute_PostTxOutcome_DecidesWhetherTheBodyOwesStateGas(bool assertionReverts, ulong expectedStateGas)
    {
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        Deploy(Asserter, assertionReverts
            ? Prepare.EvmCode.PushData(0).PushData(0).Op(Instruction.REVERT).Done
            : Prepare.EvmCode.Op(Instruction.STOP).Done);

        TestAllTracerWithOutput tracer = new();
        Transaction tx = FrameTx(nonce: 0,
            new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeSender, 0, Writer, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModePostTx, 0, Asserter, gasLimit: 200_000, UInt256.Zero, default));

        Assert.That(Process(tx, tracer).TransactionExecuted, Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.GasConsumedResult.BlockStateGas, Is.EqualTo(expectedStateGas));
            Assert.That(tracer.GasConsumedResult.EffectiveBlockGas,
                Is.EqualTo(tracer.GasConsumedResult.SpentGas - expectedStateGas),
                "the two dimensions must together account for the gas the transaction spent");
        }
    }

    [Test]
    public void Execute_WritingFrameTxAcrossTheFrameLimitsBoundary_MetersStateOnlyAfterActivation()
    {
        Address laterWriter = TestItem.AddressF;
        Deploy(Sender, ApproveCode(TxFrame.ApproveExecutionAndPayment), UInt256.Parse("100000000000000000000"));
        Deploy(Writer, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);
        Deploy(laterWriter, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done);

        TestSpecProvider provider = (TestSpecProvider)_specProvider;
        OverridableReleaseSpec specBeforeFork = new(Eip8141Prototype.Instance) { IsEip7906Enabled = true, IsEip8037Enabled = false };

        Transaction afterFork = FrameTx(nonce: 0, laterWriter);
        TestAllTracerWithOutput after = new();
        Assert.That(Process(afterFork, after).TransactionExecuted, Is.True);

        provider.GenesisSpec = specBeforeFork;
        Transaction beforeFork = FrameTx(nonce: 1, Writer);
        TestAllTracerWithOutput before = new();
        Assert.That(Process(beforeFork, before).TransactionExecuted, Is.True);

        const ulong stateCharge = (ulong)GasCostOf.SSetState;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Get(new StorageCell(Writer, (UInt256)0)).ToArray(), Is.Not.All.EqualTo((byte)0),
                "the fresh slot is written before the fork");
            Assert.That(_state.Get(new StorageCell(laterWriter, (UInt256)0)).ToArray(), Is.Not.All.EqualTo((byte)0),
                "the fresh slot is written after the fork");
            Assert.That(before.GasConsumedResult.BlockStateGas, Is.Zero,
                "before frameLimitsTime the state dimension is inert, so a fresh slot owes no state gas");
            Assert.That(after.GasConsumedResult.BlockStateGas, Is.EqualTo(stateCharge),
                "after frameLimitsTime the same fresh-slot write is billed in the state dimension");
        }
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
            new TxFrame(TxFrame.ModeSender, 0, target, executionGasLimit: 200_000, stateGasLimit: 200_000, UInt256.Zero, default));

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
