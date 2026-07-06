// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Covers fused execution of consecutive SLOAD opcodes in <c>InstructionSLoad</c>.
/// </summary>
/// <remarks>
/// Every scenario runs both fused (receipt-only tracer) and unfused (instruction/storage/access
/// tracing active, which disables fusion) and must produce identical gas, status, and output:
/// warm same-cell runs, chained cold reads, tier transitions, and out-of-gas inside a run.
/// Gas constants are for Berlin: cold SLOAD 2100, warm 100. <see cref="SLoadFusionPreBerlinTests"/>
/// pins the flat pre-hot-cold pricing branch of the fused per-op cost.
/// </remarks>
public abstract class SLoadFusionTestsBase : VirtualMachineTestsBase
{
    protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

    protected override TestAllTracerWithOutput CreateTracer()
    {
        // Keep instruction/storage tracing (disables fusion) but not access-list simulation,
        // which would pre-warm every cell and change cold/warm pricing.
        TestAllTracerWithOutput tracer = base.CreateTracer();
        tracer.IsTracingAccess = false;
        return tracer;
    }

    private sealed class ReceiptOnlyTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public long GasSpent { get; private set; }
        public byte StatusCode { get; private set; }
        public byte[]? ReturnValue { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Success;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            GasSpent = gasSpent.SpentGas;
            ReturnValue = output;
            StatusCode = Evm.StatusCode.Failure;
        }
    }

    protected (long GasSpent, byte StatusCode, byte[]? Output) Run(bool traced, long gasLimit, byte[] code)
    {
        if (traced)
        {
            TestAllTracerWithOutput result = Execute(Activation, gasLimit, code);
            return (result.GasSpent, result.StatusCode, result.ReturnValue);
        }

        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);
        ReceiptOnlyTracer tracer = new();
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return (tracer.GasSpent, tracer.StatusCode, tracer.ReturnValue);
    }

    protected static byte[] SLoadRun(int count)
    {
        Prepare prepare = Prepare.EvmCode.PushData(0);
        for (int i = 0; i < count; i++)
        {
            prepare = prepare.Op(Instruction.SLOAD);
        }
        return prepare.Done;
    }
}

public class SLoadFusionTests : SLoadFusionTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.BerlinBlockNumber;

    [TestCase(false)]
    [TestCase(true)]
    public void Same_cell_zero_value_run_charges_warm_gas_per_op(bool traced)
    {
        // SLOAD(0) -> 0, so each following SLOAD re-reads warm slot 0.
        (long gasSpent, byte statusCode, _) = Run(traced, 100000, SLoadRun(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(gasSpent, Is.EqualTo(GasCostOf.Transaction + 3 + GasCostOf.ColdSLoad + 4 * GasCostOf.WarmStateRead));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Chained_reads_price_each_new_cell_cold_then_warm(bool traced)
    {
        TestState.CreateAccount(Recipient, 0);
        TestState.Set(new StorageCell(Recipient, 0), [1]);
        TestState.Set(new StorageCell(Recipient, 1), [2]);
        TestState.Set(new StorageCell(Recipient, 2), [3]);
        TestState.Commit(MainnetSpecProvider.Instance.GenesisSpec);

        // Chain: slot0(cold)->1, slot1(cold)->2, slot2(cold)->3, slot3(cold)->0, slot0(warm)->1, slot1(warm)->2.
        (long gasSpent, byte statusCode, _) = Run(traced, 100000, SLoadRun(6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(gasSpent, Is.EqualTo(GasCostOf.Transaction + 3 + 4 * GasCostOf.ColdSLoad + 2 * GasCostOf.WarmStateRead));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Self_referential_value_switches_to_same_cell_tier(bool traced)
    {
        TestState.CreateAccount(Recipient, 0);
        TestState.Set(new StorageCell(Recipient, 0), [5]);
        TestState.Set(new StorageCell(Recipient, 5), [5]);
        TestState.Commit(MainnetSpecProvider.Instance.GenesisSpec);

        // slot0(cold)->5, slot5(cold)->5, then same-cell warm repeats; return the loaded word.
        byte[] code = Prepare.EvmCode
            .PushData(0)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .Op(Instruction.SLOAD)
            .PushData(0)
            .Op(Instruction.MSTORE)
            .PushData(32)
            .PushData(0)
            .Op(Instruction.RETURN)
            .Done;

        (long gasSpent, byte statusCode, byte[]? output) = Run(traced, 100000, code);

        byte[] expected = new byte[32];
        expected[31] = 5;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(output, Is.EqualTo(expected));
            Assert.That(gasSpent, Is.EqualTo(GasCostOf.Transaction + 3
                + 2 * GasCostOf.ColdSLoad + 2 * GasCostOf.WarmStateRead
                + 3 + GasCostOf.VeryLow + GasCostOf.Memory + 3 + 3));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Out_of_gas_inside_run_fails_at_the_same_op(bool traced)
    {
        // After PUSH(3) + cold(2100) there is 250 gas: two warm reads fit, the third does not.
        long gasLimit = GasCostOf.Transaction + 3 + GasCostOf.ColdSLoad + 250;
        (long gasSpent, byte statusCode, _) = Run(traced, gasLimit, SLoadRun(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(gasSpent, Is.EqualTo(gasLimit));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Run_reaching_code_end_halts_cleanly(bool traced)
    {
        (long gasSpent, byte statusCode, _) = Run(traced, 100000, SLoadRun(3));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(gasSpent, Is.EqualTo(GasCostOf.Transaction + 3 + GasCostOf.ColdSLoad + 2 * GasCostOf.WarmStateRead));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Exact_gas_for_full_run_succeeds_and_zero_surplus(bool traced)
    {
        long gasLimit = GasCostOf.Transaction + 3 + GasCostOf.ColdSLoad + 4 * GasCostOf.WarmStateRead;
        (long gasSpent, byte statusCode, _) = Run(traced, gasLimit, SLoadRun(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(gasSpent, Is.EqualTo(gasLimit));
        }
    }
}

/// <summary>
/// Pins the fused per-op cost on a pre-hot-cold fork (Istanbul: flat EIP-1884 SLOAD of 800),
/// covering the <c>UseHotAndColdStorage == false</c> branch of the fused pricing.
/// </summary>
public class SLoadFusionPreBerlinTests : SLoadFusionTestsBase
{
    protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;

    [TestCase(false)]
    [TestCase(true)]
    public void Same_cell_run_charges_flat_sload_cost_per_op(bool traced)
    {
        (long gasSpent, byte statusCode, _) = Run(traced, 100000, SLoadRun(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(gasSpent, Is.EqualTo(GasCostOf.Transaction + 3 + 5 * GasCostOf.SLoadEip1884));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Out_of_gas_inside_flat_priced_run_fails_at_the_same_op(bool traced)
    {
        // Two 800-gas reads fit after PUSH(3); the third does not.
        long gasLimit = GasCostOf.Transaction + 3 + 2 * GasCostOf.SLoadEip1884 + 400;
        (long gasSpent, byte statusCode, _) = Run(traced, gasLimit, SLoadRun(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(StatusCode.Failure));
            Assert.That(gasSpent, Is.EqualTo(gasLimit));
        }
    }

    // Chain steps onto a new cell each SLOAD (the loaded value is the next key), taking the fused
    // chained branch. Pre-Berlin there is no cold/warm distinction, so every distinct-cell read
    // costs a flat SLoadEip1884. Last two cases out-of-gas mid-chain (four reads fit, the fifth
    // does not), pinning the chained OOG path that reports the failing op exactly like dispatch.
    [TestCase(false, 100000L, GasCostOf.Transaction + 3 + 6 * GasCostOf.SLoadEip1884, StatusCode.Success)]
    [TestCase(true, 100000L, GasCostOf.Transaction + 3 + 6 * GasCostOf.SLoadEip1884, StatusCode.Success)]
    [TestCase(false, GasCostOf.Transaction + 3 + 4 * GasCostOf.SLoadEip1884 + 400, GasCostOf.Transaction + 3 + 4 * GasCostOf.SLoadEip1884 + 400, StatusCode.Failure)]
    [TestCase(true, GasCostOf.Transaction + 3 + 4 * GasCostOf.SLoadEip1884 + 400, GasCostOf.Transaction + 3 + 4 * GasCostOf.SLoadEip1884 + 400, StatusCode.Failure)]
    public void Chained_reads_price_flat_per_new_cell(bool traced, long gasLimit, long expectedGasSpent, byte expectedStatus)
    {
        TestState.CreateAccount(Recipient, 0);
        TestState.Set(new StorageCell(Recipient, 0), [1]);
        TestState.Set(new StorageCell(Recipient, 1), [2]);
        TestState.Set(new StorageCell(Recipient, 2), [3]);
        TestState.Commit(MainnetSpecProvider.Instance.GenesisSpec);

        // Chain: slot0->1, slot1->2, slot2->3, slot3->0, slot0->1, slot1->2 (each a distinct cell).
        (long gasSpent, byte statusCode, _) = Run(traced, gasLimit, SLoadRun(6));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(statusCode, Is.EqualTo(expectedStatus));
            Assert.That(gasSpent, Is.EqualTo(expectedGasSpent));
        }
    }
}
