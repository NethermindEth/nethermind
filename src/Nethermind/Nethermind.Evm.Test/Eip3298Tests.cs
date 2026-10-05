// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-3298: the SSTORE storage-clear refund and the EIP-3529 refund cap are removed on top of
/// EIP-8037/EIP-8038; only the net-metered STORAGE_WRITE reversal remains.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
public class Eip3298Tests(bool eip3298Enabled) : VirtualMachineTestsBase
{
    private const ulong GasLimit = 1_000_000;
    private const ulong StorageWrite = Eip8038Constants.StorageWrite;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => MainnetSpecProvider.AmsterdamBlockTimestamp;
    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Amsterdam.Instance) { IsEip3298Enabled = eip3298Enabled });

    private delegate RefundResult RefundInvoker(Transaction tx, BlockHeader header, IReleaseSpec spec, ExecutionOptions opts,
        in TransactionSubstate substate, in EthereumGasPolicy gas, in UInt256 gasPrice, ulong codeInsertRefunds,
        in EthereumGasPolicy floorGas, in EthereumGasPolicy intrinsicGas, long postIntrinsicStateReservoir, bool topLevelCreateStateGasCharged);

    [Test]
    public void Refund_rejects_a_counter_exceeding_pre_refund_gas()
    {
        // Valid execution cannot violate this invariant; inject a corrupt substate into the production refund path.
        MethodInfo method = typeof(TransactionProcessorBase<EthereumGasPolicy>).GetMethod("Refund", BindingFlags.Instance | BindingFlags.NonPublic)!;
        RefundInvoker refund = method.CreateDelegate<RefundInvoker>(_processor);
        Transaction tx = Build.A.Transaction.WithGasLimit(GasLimit).WithSenderAddress(Sender).TestObject;
        TransactionSubstate substate = new(default, (long)GasLimit + 1, null, null, false);
        EthereumGasPolicy gas = default;
        UInt256 price = UInt256.Zero;

        RefundResult result = refund(tx, Build.A.BlockHeader.TestObject, Spec, ExecutionOptions.Commit, in substate, in gas, in price,
            0, in gas, in gas, 0, false);

        Assert.That(result.Result.Error, Is.EqualTo(eip3298Enabled ? TransactionResult.ErrorType.StateGasInvariantViolated : TransactionResult.ErrorType.None));
    }

    [Test]
    public void Frame_refund_rejects_invalid_counters_and_restores_state([Values(-1L, 101L)] long refundCounter)
    {
        // Before EIP-3298 a negative counter is guarded by a Debug.Assert, not a Release check.
        Assume.That(eip3298Enabled || refundCounter >= 0);
        MethodInfo method = typeof(TransactionProcessorBase<EthereumGasPolicy>).GetMethod("SettleFrameTx", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(OffFlag));
        Transaction tx = Build.A.Transaction.WithGasLimit(GasLimit).WithSenderAddress(Sender).TestObject;
        FrameTxContext context = new(Sender, 0, [], [], default, default, default, default, default, default) { Payer = Sender };
        using StackAccessTracker tracker = new();
        TestState.CreateAccount(Sender, 10);
        TestState.Commit(Spec);
        UInt256 initialBalance = TestState.GetBalance(Sender);
        Snapshot snapshot = TestState.TakeSnapshot();
        TestState.SubtractFromBalance(Sender, UInt256.One, Spec);
        object[] args = [tx, NullTxTracer.Instance, null!, ExecutionOptions.Restore, Build.A.BlockHeader.TestObject, Spec,
            context, Array.Empty<TxFrameReceipt>(), tracker, snapshot, 0UL, 0UL, 100UL, 0L, refundCounter,
            UInt256.Zero, UInt256.Zero, UInt256.Zero, UInt256.Zero, false];

        TransactionResult result = (TransactionResult)method.Invoke(_processor, args)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Error, Is.EqualTo(eip3298Enabled
                ? TransactionResult.ErrorType.StateGasInvariantViolated
                : TransactionResult.ErrorType.None));
            Assert.That(TestState.GetBalance(Sender), Is.EqualTo(initialBalance));
        }
    }

    // The EIP's Test Cases table (x = 1, y = 2, z = 3), with the EIP-8038 STORAGE_CLEAR_REFUND net count it strikes.
    [TestCase((byte)0, new byte[] { 1 }, 0, 0, TestName = "0 -> x")]
    [TestCase((byte)0, new byte[] { 1, 0 }, 1, 0, TestName = "0 -> x -> 0")]
    [TestCase((byte)1, new byte[] { 0 }, 0, 1, TestName = "x -> 0")]
    [TestCase((byte)1, new byte[] { 0, 1 }, 1, 0, TestName = "x -> 0 -> x")]
    [TestCase((byte)1, new byte[] { 2, 1 }, 1, 0, TestName = "x -> y -> x")]
    [TestCase((byte)1, new byte[] { 2, 0 }, 0, 1, TestName = "x -> y -> 0")]
    [TestCase((byte)1, new byte[] { 2, 1, 3 }, 1, 0, TestName = "x -> y -> x -> z")]
    [TestCase((byte)1, new byte[] { 0, 1, 0 }, 1, 1, TestName = "x -> 0 -> x -> 0")]
    public void Sstore_sequence_refunds_only_write_reversals(byte original, byte[] writes, int writeReversals, int netClears)
    {
        SetSlots(original, 1);
        Prepare code = Prepare.EvmCode;
        foreach (byte value in writes)
        {
            code.PushData(value).PushData(0).Op(Instruction.SSTORE);
        }

        TestAllTracerWithOutput result = Execute(Activation, GasLimit, code.Done);

        ulong refundCounter = (ulong)writeReversals * StorageWrite
            + (eip3298Enabled ? 0 : (ulong)netClears * RefundOf.SClearEip8038);
        AssertSettlement(result, refundCounter);
        AssertStorage(new StorageCell(Recipient, 0), writes[^1]);
    }

    [Test]
    public void Clearing_many_slots_grants_no_refund([Values(1, 16)] int slots)
    {
        SetSlots(1, slots);
        Prepare code = Prepare.EvmCode;
        for (int slot = 0; slot < slots; slot++)
        {
            code.PushData(0).PushData(slot).Op(Instruction.SSTORE);
        }

        TestAllTracerWithOutput result = Execute(Activation, GasLimit, code.Done);

        AssertSettlement(result, eip3298Enabled ? 0 : (ulong)slots * RefundOf.SClearEip8038);
    }

    [Test]
    public void Refund_above_a_fifth_of_gas_used_is_not_capped_and_block_gas_stays_pre_refund()
    {
        const int slots = 3;
        TestAllTracerWithOutput result = Execute(Activation, GasLimit, RestoreSlots(slots));

        ulong refundCounter = slots * StorageWrite;
        ulong preRefundGas = result.GasConsumedResult.MaxUsedGas;
        Assert.That(refundCounter, Is.GreaterThan(preRefundGas / RefundHelper.MaxRefundQuotientEIP3529), "scenario must exceed the EIP-3529 cap");
        AssertSettlement(result, refundCounter);
        using (Assert.EnterMultipleScope())
        {
            // EIP-7778: refunds reduce what the sender pays, not the block's execution gas.
            Assert.That(result.GasConsumedResult.BlockGas, Is.EqualTo(preRefundGas));
            Assert.That(result.CumulativeExecutionGasUsed, Is.EqualTo(preRefundGas));
        }
    }

    [Test]
    public void Calldata_floor_binds_after_an_uncapped_refund()
    {
        const int slots = 3;
        byte[] calldata = Enumerable.Repeat((byte)0xff, 300).ToArray();
        (Block block, Transaction tx) = PrepareTx(Activation, GasLimit, RestoreSlots(slots), calldata, UInt256.One);
        ulong floorGas = EthereumGasPolicy.CalculateIntrinsicGas(tx, Spec).FloorGas.Value;

        TestAllTracerWithOutput result = CreateTracer();
        _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), result);

        ulong refundCounter = slots * StorageWrite;
        ulong preRefundGas = result.GasConsumedResult.MaxUsedGas;
        ulong cappedRefund = preRefundGas / RefundHelper.MaxRefundQuotientEIP3529;
        Assert.That(floorGas, Is.InRange(preRefundGas - refundCounter + 1, preRefundGas - cappedRefund - 1),
            "floor must bind only once the full refund is applied");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.GasSpent, Is.EqualTo(eip3298Enabled ? floorGas : preRefundGas - cappedRefund));
            Assert.That(result.GasConsumedResult.BlockGas, Is.EqualTo(preRefundGas));
        }
    }

    private void SetSlots(byte value, int count)
    {
        TestState.CreateAccount(Recipient, 1.Ether);
        for (int slot = 0; slot < count; slot++)
        {
            TestState.Set(new StorageCell(Recipient, (UInt256)slot), new UInt256(value));
        }

        TestState.Commit(SpecProvider.GenesisSpec);
    }

    // x -> y -> x on each slot: one STORAGE_WRITE charge and one reversal refund per slot.
    private byte[] RestoreSlots(int slots)
    {
        SetSlots(1, slots);
        Prepare code = Prepare.EvmCode;
        for (int slot = 0; slot < slots; slot++)
        {
            code.PushData(2).PushData(slot).Op(Instruction.SSTORE)
                .PushData(1).PushData(slot).Op(Instruction.SSTORE);
        }

        return code.Done;
    }

    private void AssertSettlement(TestAllTracerWithOutput result, ulong refundCounter)
    {
        // Calldata-free calls stay above the floor, so MaxUsedGas is the pre-refund gas.
        ulong preRefundGas = result.GasConsumedResult.MaxUsedGas;
        ulong appliedRefund = eip3298Enabled
            ? refundCounter
            : Math.Min(preRefundGas / RefundHelper.MaxRefundQuotientEIP3529, refundCounter);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.Refund, Is.EqualTo((long)refundCounter), "refund counter");
            Assert.That(result.GasConsumedResult.GasRefund, Is.EqualTo(appliedRefund), "applied refund");
            Assert.That(result.GasSpent, Is.EqualTo(preRefundGas - appliedRefund), "gas used");
        }
    }
}

/// <summary>EIP-3298 gating of the storage-clear refund and refund cap, including forks without EIP-8038.</summary>
public class Eip3298SpecTests
{
    private static readonly IReleaseSpec[] Forks = [Frontier.Instance, Cancun.Instance, Amsterdam.Instance];

    [Test]
    public void Storage_clear_refund_and_cap_are_removed([ValueSource(nameof(Forks))] IReleaseSpec fork, [Values] bool eip3298Enabled)
    {
        OverridableReleaseSpec spec = new(fork) { IsEip3298Enabled = eip3298Enabled };
        const ulong spentGas = 100_000;
        const ulong refund = 90_000;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.GasCosts.SClearRefund, eip3298Enabled ? Is.Zero : Is.EqualTo(fork.GasCosts.SClearRefund).And.Not.Zero);
            Assert.That(spec.GasCosts.DestroyRefund, eip3298Enabled ? Is.Zero : Is.EqualTo(fork.GasCosts.DestroyRefund));
            Assert.That(RefundHelper.CalculateClaimableRefund(spentGas, refund, spec),
                Is.EqualTo(eip3298Enabled ? refund : spentGas / (fork.IsEip3529Enabled ? RefundHelper.MaxRefundQuotientEIP3529 : RefundHelper.MaxRefundQuotient)));
        }
    }
}

/// <summary>
/// EIP-3298 enabled on a pre-EIP-3529 base: without the cap, the SELFDESTRUCT refund would exceed the gas it
/// is netted against, so it is removed along with the storage-clear refund.
/// </summary>
[TestFixture(true)]
[TestFixture(false)]
public class Eip3298PreLondonTests(bool eip3298Enabled) : VirtualMachineTestsBase
{
    private const int Contracts = 3;

    protected override ulong BlockNumber => MainnetSpecProvider.BerlinBlockNumber;
    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Berlin.Instance) { IsEip3298Enabled = eip3298Enabled });

    [Test]
    public void Selfdestruct_refund_does_not_exceed_gas_used()
    {
        Prepare code = Prepare.EvmCode;
        byte[] selfDestruct = Prepare.EvmCode.PushData(Miner).Op(Instruction.SELFDESTRUCT).Done;
        for (int i = 0; i < Contracts; i++)
        {
            Address contract = Address.FromNumber((UInt256)(0x1000 + i));
            TestState.CreateAccount(contract, 0);
            TestState.InsertCode(contract, selfDestruct, Spec);
            code.Call(contract, 50_000);
        }

        TestState.Commit(Spec);

        TestAllTracerWithOutput result = Execute(Activation, 1_000_000, code.Done);

        ulong preRefundGas = result.GasConsumedResult.MaxUsedGas;
        ulong expectedRefund = eip3298Enabled
            ? 0
            : Math.Min(preRefundGas / RefundHelper.MaxRefundQuotient, Contracts * RefundOf.DestroyBeforeEip3529);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.GasConsumedResult.GasRefund, Is.EqualTo(expectedRefund));
            Assert.That(result.GasSpent, Is.EqualTo(preRefundGas - expectedRefund));
        }
    }
}
