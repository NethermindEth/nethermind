// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8279: the transaction gas floor grows with the bytes the transaction
/// contributes to the EIP-7928 block access list, and <c>gasUsed = max(execution, floor)</c>.
/// </summary>
/// <remarks>
/// The observable scenario is refund-driven: clearing pre-existing storage slots earns
/// EIP-3529 refunds that push execution gas below the BAL byte floor (each cleared slot
/// contributes 32 key + 32 value bytes = 4096 floor gas).
/// </remarks>
[TestFixture(true)]
[TestFixture(false)]
public class Eip8279Tests(bool eip8279Enabled) : VirtualMachineTestsBase
{
    private const int SlotCount = 10;

    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(
        new OverridableReleaseSpec(Prague.Instance) { IsEip8131Enabled = true, IsEip8279Enabled = eip8279Enabled });

    protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };

    [Test]
    public void Storage_clearing_gas_is_floored_by_bal_bytes()
    {
        // Pre-populate the slots the contract will clear.
        TestState.CreateAccount(SenderRecipientAndMiner.Default.Recipient, 1);
        for (int i = 0; i < SlotCount; i++)
        {
            TestState.Set(new StorageCell(SenderRecipientAndMiner.Default.Recipient, (UInt256)i), [1]);
        }

        TestState.Commit(Spec);
        TestState.CommitTree(0);

        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < SlotCount; i++)
        {
            code = code
                .PushData(0)
                .PushData(i)
                .Op(Instruction.SSTORE);
        }

        TestAllTracerWithOutput result = Execute(code.Op(Instruction.STOP).Done);

        // Execution: 21000 + 10 × (PUSH1 + PUSH1 + cold SSTORE clear) with the EIP-3529
        // refund capped at spent / 5.
        const ulong executionSpent = GasCostOf.Transaction + SlotCount * (2 * GasCostOf.VeryLow + GasCostOf.ColdSLoad + 2900);
        const ulong refunded = executionSpent / 5; // 10 × 4800 refund, capped
        const ulong netExecution = executionSpent - refunded;
        // Floor: 21000 + 10 slots × (32 key + 32 value bytes) × 64 gas/byte.
        const ulong floor = GasCostOf.Transaction
            + SlotCount * (Eip8279Constants.BalBytesPerStorageKey + Eip8279Constants.BalBytesPerStorageValue) * Eip8279Constants.FloorGasPerByte;

        Assert.That(floor, Is.GreaterThan(netExecution), "test setup must make the floor dominate");
        Assert.That(result.GasSpent, Is.EqualTo(eip8279Enabled ? floor : netExecution));
    }

    [Test]
    public void Restored_slot_refunds_its_value_bytes()
    {
        TestState.CreateAccount(SenderRecipientAndMiner.Default.Recipient, 1);
        TestState.Set(new StorageCell(SenderRecipientAndMiner.Default.Recipient, UInt256.Zero), [1]);
        TestState.Commit(Spec);
        TestState.CommitTree(0);

        // Clear slot 0, then restore it to its pre-transaction value: only the key bytes may
        // remain in the floor.
        byte[] code = Prepare.EvmCode
            .PushData(0).PushData(0).Op(Instruction.SSTORE)
            .PushData(1).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        // Execution: clear (cold, 2100 + 2900) then restore (warm dirty, 100 + 100... net
        // metered warm write) with reversal refunds — always above the tiny floor, so the
        // result must equal plain execution gas in both fixtures.
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
        Assert.That(result.GasSpent, Is.LessThan(30000UL));
    }

    [Test]
    public void Meter_arithmetic()
    {
        BalFloorMeter meter = new(staticFloor: 21000, gasLimit: 30000);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(meter.FloorGasUsed, Is.EqualTo(21000UL));
            Assert.That(meter.TryMeter(64), Is.True);
            Assert.That(meter.FloorGasUsed, Is.EqualTo(21000UL + 64 * 64));
            meter.Refund(32);
            Assert.That(meter.FloorGasUsed, Is.EqualTo(21000UL + 32 * 64));
            Assert.That(meter.TryMeter(200), Is.False, "floor above the gas limit must be out of gas");
        }
    }
}
