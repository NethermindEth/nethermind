// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
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
            Assert.That(RefundHelper.CalculateClaimableRefund(spentGas, refund, spec),
                Is.EqualTo(eip3298Enabled ? refund : spentGas / (fork.IsEip3529Enabled ? RefundHelper.MaxRefundQuotientEIP3529 : RefundHelper.MaxRefundQuotient)));
        }
    }
}
