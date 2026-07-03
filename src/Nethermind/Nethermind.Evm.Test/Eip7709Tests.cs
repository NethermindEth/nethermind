// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
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
/// Tests for EIP-7709: BLOCKHASH served from the EIP-2935 history contract with full SLOAD
/// semantics (cold/warm charge and slot warming) for in-window queries; out-of-window queries
/// return zero at base cost.
/// </summary>
[TestFixture]
public class Eip7709Tests : VirtualMachineTestsBase
{
    private const ulong CurrentBlockNumber = 300;
    private static readonly Hash256 StoredHash = TestItem.KeccakA;

    protected override ulong BlockNumber => CurrentBlockNumber;
    protected override ISpecProvider SpecProvider { get; } =
        new TestSpecProvider(new OverridableReleaseSpec(Prague.Instance) { IsEip7709Enabled = true });

    // Access tracing pre-warms every touched cell, which would hide the cold/warm distinction
    // these tests assert.
    protected override TestAllTracerWithOutput CreateTracer() => new() { IsTracingAccess = false };

    public override void Setup()
    {
        base.Setup();
        TestState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 1);
        TestState.Set(
            new StorageCell(Eip2935Constants.BlockHashHistoryAddress, new UInt256((CurrentBlockNumber - 1) % Eip2935Constants.RingBufferSize)),
            StoredHash.BytesToArray());
        TestState.Commit(SpecProvider.GenesisSpec);
        TestState.CommitTree(0);
    }

    [Test]
    public void In_window_query_returns_stored_hash_and_charges_cold_sload()
    {
        byte[] code = Prepare.EvmCode
            .PushData(CurrentBlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReturnValue, Is.EqualTo(StoredHash.BytesToArray()));
            // 21000 + PUSH1 + BLOCKHASH base + cold SLOAD + PUSH1 + MSTORE(+memory) + PUSH1 + PUSH1 + RETURN
            Assert.That(result.GasSpent, Is.EqualTo(
                GasCostOf.Transaction
                + GasCostOf.VeryLow * 4
                + GasCostOf.BlockHash + GasCostOf.ColdSLoad
                + GasCostOf.VeryLow + GasCostOf.Memory));
        }
    }

    [Test]
    public void Second_query_of_same_slot_is_warm()
    {
        byte[] code = Prepare.EvmCode
            .PushData(CurrentBlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .PushData(CurrentBlockNumber - 1)
            .Op(Instruction.BLOCKHASH)
            .Op(Instruction.POP)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        Assert.That(result.GasSpent, Is.EqualTo(
            GasCostOf.Transaction
            + 2 * (GasCostOf.VeryLow + GasCostOf.BlockHash + GasCostOf.Base)
            + GasCostOf.ColdSLoad + GasCostOf.WarmStateRead));
    }

    [Test]
    public void Out_of_window_query_returns_zero_at_base_cost()
    {
        // 300 - 257 = 43 is older than BLOCKHASH_SERVE_WINDOW even though within the 2935 ring.
        byte[] code = Prepare.EvmCode
            .PushData(CurrentBlockNumber - Eip7709Constants.BlockHashServeWindow - 1)
            .Op(Instruction.BLOCKHASH)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        TestAllTracerWithOutput result = Execute(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ReturnValue, Is.EqualTo(new byte[32]));
            Assert.That(result.GasSpent, Is.EqualTo(
                GasCostOf.Transaction
                + GasCostOf.VeryLow * 4
                + GasCostOf.BlockHash
                + GasCostOf.VeryLow + GasCostOf.Memory));
        }
    }
}
