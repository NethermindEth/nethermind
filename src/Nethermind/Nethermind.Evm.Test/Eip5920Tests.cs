// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Blockchain.Tracing.GethStyle.Custom.Native.Prestate;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-5920 PAY opcode, run on a pre-Amsterdam fork (EIP-2929 costs) and on Amsterdam, where the value and
/// new-account surcharges follow CALL's under EIP-2780/EIP-8037/EIP-8038 and transfers are logged (EIP-7708).
/// </summary>
[TestFixture(false)]
[TestFixture(true)]
public class Eip5920Tests(bool amsterdam) : VirtualMachineTestsBase
{
    private const ulong GasLimit = 1_000_000;
    private static readonly Address Existing = TestItem.AddressC;
    private static readonly Address Missing = TestItem.AddressF;
    private static readonly UInt256 ContractBalance = 100.Ether + 1;

    protected override ISpecProvider SpecProvider { get; } = new TestSpecProvider(
        new OverridableReleaseSpec(amsterdam ? Amsterdam.Instance : Prague.Instance) { IsEip5920Enabled = true });

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => amsterdam ? MainnetSpecProvider.AmsterdamBlockTimestamp : MainnetSpecProvider.PragueBlockTimestamp;

    private ulong ColdAccess => amsterdam ? Eip8038Constants.ColdAccountAccess : GasCostOf.ColdAccountAccess;
    private ulong ValueCost => amsterdam ? Eip8038Constants.CallValue : GasCostOf.CallValue;
    private ulong NewAccountCost => amsterdam ? (ulong)GasCostOf.NewAccountState : GasCostOf.NewAccount;

    protected override TestAllTracerWithOutput CreateTracer() => new LogTracer();

    /// <summary>PAYs <paramref name="value"/> to <paramref name="target"/> and returns the pushed status word.</summary>
    private static byte[] PayAndReturnStatus(Address target, UInt256 value) =>
        Pay(Prepare.EvmCode, target, value).PushData(0).Op(Instruction.MSTORE).PushData(32).PushData(0).Op(Instruction.RETURN).Done;

    private static Prepare Pay(Prepare code, Address target, UInt256 value) =>
        code.PushData(value).PushData(target).Op(Instruction.PAY);

    private LogTracer Run(byte[] code) => (LogTracer)Execute(Activation, GasLimit, code);

    private static UInt256 Status(TestAllTracerWithOutput result) => new(result.ReturnValue, isBigEndian: true);

    private void AssertSucceeded(TestAllTracerWithOutput result) =>
        Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success), result.Error);

    private ulong PayCost(byte[] code) => OpcodeCost(code, Instruction.PAY);

    // Gas charged at the opcode's step, per the Geth-style trace.
    private ulong OpcodeCost(byte[] code, Instruction opcode)
    {
        foreach (GethTxTraceEntry entry in ExecuteAndTrace(GasLimit, code).Entries)
        {
            if (entry.Opcode == opcode.ToString()) return entry.GasCost;
        }

        throw new InvalidOperationException($"{opcode} was not traced");
    }

    [Test]
    public void Pay_transfers_value_and_pushes_one()
    {
        TestState.CreateAccount(Existing, 5);

        LogTracer result = Run(PayAndReturnStatus(Existing, 7));

        AssertSucceeded(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Status(result), Is.EqualTo(UInt256.One));
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo((UInt256)12));
            Assert.That(TestState.GetBalance(Recipient), Is.EqualTo(ContractBalance - 7));
            result.Logs.AssertEquivalentTo(ExpectedLogs(ExpectedTransferLog(Recipient, Existing, 7)));
        }
    }

    [TestCase(true, TestName = "Insufficient balance to an existing account pushes zero")]
    [TestCase(false, TestName = "Insufficient balance to a missing account pushes zero")]
    public void Pay_with_insufficient_balance_pushes_zero_and_moves_nothing(bool targetExists)
    {
        Address target = targetExists ? Existing : Missing;
        if (targetExists) TestState.CreateAccount(target, 5);
        LogTracer result = Run(PayAndReturnStatus(target, UInt256.MaxValue));

        AssertSucceeded(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Status(result), Is.EqualTo(UInt256.Zero));
            Assert.That(TestState.GetBalance(Recipient), Is.EqualTo(ContractBalance));
            Assert.That(TestState.AccountExists(target), Is.EqualTo(targetExists));
            result.Logs.AssertEquivalentTo(ExpectedLogs());
        }
    }

    [TestCase(false, TestName = "Cold target pays the cold access cost")]
    [TestCase(true, TestName = "Warm target pays the warm access cost")]
    public void Pay_charges_warm_or_cold_access(bool warmFirst)
    {
        TestState.CreateAccount(Existing, 5);
        Prepare code = Prepare.EvmCode;
        if (warmFirst) code = code.PushData(Existing).Op(Instruction.BALANCE).Op(Instruction.POP);

        ulong cost = PayCost(Pay(code, Existing, 0).STOP().Done);

        Assert.That(cost, Is.EqualTo(warmFirst ? GasCostOf.WarmStateRead : ColdAccess));
    }

    [Test]
    public void Pay_warms_the_target()
    {
        TestState.CreateAccount(Existing, 5);
        byte[] code = Pay(Prepare.EvmCode, Existing, 0).Op(Instruction.POP)
            .PushData(Existing).Op(Instruction.BALANCE).STOP().Done;

        Assert.That(OpcodeCost(code, Instruction.BALANCE), Is.EqualTo(GasCostOf.WarmStateRead));
    }

    private static IEnumerable<TestCaseData> SurchargeCases()
    {
        yield return new TestCaseData(true, 0, false, false).SetName("Zero value to an existing account pays access only");
        yield return new TestCaseData(false, 0, false, false).SetName("Zero value to a missing account pays access only");
        yield return new TestCaseData(true, 1, true, false).SetName("Value to an existing account adds the value cost");
        yield return new TestCaseData(false, 1, true, true).SetName("Value to a missing account adds the value and new-account costs");
    }

    [TestCaseSource(nameof(SurchargeCases))]
    public void Pay_charges_value_and_new_account_surcharges(bool targetExists, int value, bool valueCharged, bool newAccountCharged)
    {
        Address target = targetExists ? Existing : Missing;
        if (targetExists) TestState.CreateAccount(target, 5);

        ulong cost = PayCost(Pay(Prepare.EvmCode, target, (UInt256)value).STOP().Done);

        ulong expected = ColdAccess + (valueCharged ? ValueCost : 0) + (newAccountCharged ? NewAccountCost : 0);
        Assert.That(cost, Is.EqualTo(expected));
    }

    [Test]
    public void Pay_with_insufficient_balance_to_missing_account_refunds_only_state_gas()
    {
        // As with CALL, a NEW_ACCOUNT charged as EIP-8037 state gas is refilled when no account is created;
        // the legacy execution-gas charge is not.
        byte[] code = Pay(Prepare.EvmCode, Missing, UInt256.MaxValue).STOP().Done;
        byte[] reference = Pay(Prepare.EvmCode, Existing, UInt256.MaxValue).STOP().Done;
        TestState.CreateAccount(Existing, 5);

        ulong missingGas = Run(code).GasSpent;
        ulong existingGas = Run(reference).GasSpent;

        Assert.That(missingGas - existingGas, Is.EqualTo(amsterdam ? 0 : GasCostOf.NewAccount));
    }

    [Test]
    public void Zero_value_pay_to_missing_account_pushes_one_and_creates_nothing()
    {
        LogTracer result = Run(PayAndReturnStatus(Missing, 0));

        AssertSucceeded(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Status(result), Is.EqualTo(UInt256.One));
            Assert.That(TestState.AccountExists(Missing), Is.False);
            result.Logs.AssertEquivalentTo(ExpectedLogs());
        }
    }

    [TestCase(true, TestName = "Self-payment within balance pushes one")]
    [TestCase(false, TestName = "Self-payment above balance pushes zero")]
    public void Self_payment_moves_nothing(bool withinBalance)
    {
        UInt256 value = withinBalance ? 7 : UInt256.MaxValue;

        LogTracer result = Run(PayAndReturnStatus(Recipient, value));

        AssertSucceeded(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Status(result), Is.EqualTo(withinBalance ? UInt256.One : UInt256.Zero));
            Assert.That(TestState.GetBalance(Recipient), Is.EqualTo(ContractBalance));
            result.Logs.AssertEquivalentTo(ExpectedLogs());
        }
    }

    [Test]
    public void Self_payment_pays_the_warm_access_and_value_costs()
    {
        ulong cost = PayCost(Pay(Prepare.EvmCode, Recipient, 7).STOP().Done);

        Assert.That(cost, Is.EqualTo(GasCostOf.WarmStateRead + ValueCost));
    }

    [TestCase(false, TestName = "Contract target code is not executed")]
    [TestCase(true, TestName = "Delegated target code is not executed or resolved")]
    public void Pay_does_not_execute_target_code(bool delegated)
    {
        // The target's code would write slot 0; PAY must credit it without running it.
        Address codeHolder = TestItem.AddressE;
        byte[] writer = Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).STOP().Done;
        TestState.CreateAccount(Existing, 5);
        TestState.CreateAccount(codeHolder, 0);
        TestState.InsertCode(codeHolder, writer, Spec);
        TestState.InsertCode(Existing, delegated ? [.. Eip7702Constants.DelegationHeader, .. codeHolder.Bytes] : writer, Spec);

        LogTracer result = Run(PayAndReturnStatus(Existing, 7));
        UInt256 targetBalance = TestState.GetBalance(Existing);
        ulong cost = PayCost(Pay(Prepare.EvmCode, Existing, 7).STOP().Done);

        AssertSucceeded(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Status(result), Is.EqualTo(UInt256.One));
            Assert.That(targetBalance, Is.EqualTo((UInt256)12));
            Assert.That(StorageAt(Existing), Is.EqualTo(UInt256.Zero));
            Assert.That(StorageAt(codeHolder), Is.EqualTo(UInt256.Zero));
            Assert.That(cost, Is.EqualTo(ColdAccess + ValueCost), "the delegation target is not accessed");
        }
    }

    [TestCase(true, TestName = "Successful PAY is reported as a codeless zero-gas CALL")]
    [TestCase(false, TestName = "PAY with insufficient balance reports no action")]
    public void Pay_is_reported_to_action_tracers(bool withinBalance)
    {
        TestState.CreateAccount(Existing, 5);
        UInt256 value = withinBalance ? 7 : UInt256.MaxValue;

        LogTracer result = Run(Pay(Prepare.EvmCode, Existing, value).STOP().Done);

        AssertSucceeded(result);
        TestAllTracerWithOutput.ActionTrace[] payActions = result.Actions.Skip(1).ToArray();
        Assert.That(payActions, withinBalance
            ? Is.EqualTo(new[] { new TestAllTracerWithOutput.ActionTrace(0, value, Recipient, Existing, ExecutionType.CALL, false) })
            : Is.Empty);
    }

    [Test]
    public void Parity_trace_shows_pay_as_a_call_subtrace()
    {
        TestState.CreateAccount(Existing, 5);
        (Block block, Transaction transaction) = PrepareTx(Activation, GasLimit, Pay(Prepare.EvmCode, Existing, 7).STOP().Done);
        ParityLikeTxTracer tracer = new(block, transaction, ParityTraceTypes.Trace | ParityTraceTypes.VmTrace);

        _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);

        ParityTraceAction pay = tracer.BuildResult().Action!.Subtraces.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((pay.CallType, pay.From, pay.To, pay.Value), Is.EqualTo(("call", Recipient, Existing, (UInt256)7)));
            Assert.That(pay.Result?.GasUsed, Is.EqualTo(0UL));
        }
    }

    [Test]
    public void Parity_vm_trace_reports_pay_cost_and_push([Values] bool streaming)
    {
        TestState.CreateAccount(Existing, 5);
        byte[] code = Pay(Prepare.EvmCode, Existing, 7).Op(Instruction.POP).STOP().Done;

        IReadOnlyList<(ulong Cost, bool HasSubtrace, int Pushes)> operations = TraceParityVmOperations(code, streaming);

        // PUSH value, PUSH target, PAY, POP, STOP
        Assert.That(operations.Select(static op => (op.Cost, op.Pushes)), Is.EqualTo(new[]
        {
            (GasCostOf.VeryLow, 1), (GasCostOf.VeryLow, 1), (ColdAccess + ValueCost, 1), (GasCostOf.Base, 0), (GasCostOf.Free, 0),
        }));
    }

    [TestCase(0, TestName = "Zero-value PAY in a static frame halts")]
    [TestCase(1, TestName = "Value-bearing PAY in a static frame halts")]
    public void Pay_in_static_frame_halts(int value)
    {
        // EIP-214: the static frame halts, so STATICCALL pushes 0 and the target is not paid.
        TestState.CreateAccount(Existing, 5);
        TestState.CreateAccount(TestItem.AddressE, 100);
        TestState.InsertCode(TestItem.AddressE, Pay(Prepare.EvmCode, Existing, (UInt256)value).STOP().Done, Spec);
        byte[] code = Prepare.EvmCode.StaticCall(TestItem.AddressE, 100_000)
            .PushData(0).Op(Instruction.MSTORE).PushData(32).PushData(0).Op(Instruction.RETURN).Done;

        LogTracer result = Run(code);

        AssertSucceeded(result);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Status(result), Is.EqualTo(UInt256.Zero));
            Assert.That(result.ReportedActionErrors, Does.Contain(EvmExceptionType.StaticCallViolation));
            Assert.That(TestState.GetBalance(Existing), Is.EqualTo((UInt256)5));
        }
    }

    [TestCase("0000000000000000000000010000000000000000000000000000000000000000", true, TestName = "Lowest high byte set halts")]
    [TestCase("8000000000000000000000000000000000000000000000000000000000000000", true, TestName = "Top bit set halts")]
    [TestCase("000000000000000000000000ffffffffffffffffffffffffffffffffffffffff", false, TestName = "Largest 20-byte address is paid")]
    public void Pay_halts_when_high_address_bytes_are_set(string addressWord, bool halts)
    {
        byte[] word = Bytes.FromHexString(addressWord);
        Address truncated = new(word.AsSpan(12));
        byte[] code = Prepare.EvmCode.PushData(1).PushData(word).Op(Instruction.PAY).STOP().Done;

        LogTracer result = Run(code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(halts ? StatusCode.Failure : StatusCode.Success), result.Error);
            Assert.That(result.GasSpent, halts ? Is.EqualTo(GasLimit) : Is.LessThan(GasLimit));
            Assert.That(TestState.GetBalance(truncated), Is.EqualTo(halts ? UInt256.Zero : UInt256.One));
        }
    }

    [Test]
    public void Prestate_trace_includes_the_pay_target()
    {
        TestState.CreateAccount(Existing, 5);
        NativePrestateTracer tracer = new(TestState, GethTraceOptions.Default, Hash256.Zero, Sender, Recipient);

        GethLikeTxTrace trace = Execute(tracer, Pay(Prepare.EvmCode, Existing, 7).STOP().Done).BuildResult();

        Assert.That(new EthereumJsonSerializer().Serialize(trace.CustomTracerResult), Does.Contain(Existing.ToString()));
    }

    private static IEnumerable<TestCaseData> BalCases()
    {
        yield return new TestCaseData(7, true, false).SetName("Value transfer records the target balance change");
        yield return new TestCaseData(0, false, false).SetName("Zero-value PAY records the target as a pure read");
        yield return new TestCaseData(7, false, true).SetName("Out of gas at the access charge leaves the target out");
    }

    [TestCaseSource(nameof(BalCases))]
    public void Pay_records_the_target_in_the_block_access_list(int value, bool balanceChanges, bool outOfGasBeforeAccess)
    {
        if (!amsterdam) Assert.Ignore("EIP-7928 is only enabled on the Amsterdam fixture");

        TestState.CreateAccount(Existing, 5);
        byte[] code = outOfGasBeforeAccess
            // The inner frame gets two PUSHes and one gas short of PAY's state-independent charges.
            ? Prepare.EvmCode.Call(TestItem.AddressE, (long)(2 * GasCostOf.VeryLow + ColdAccess + ValueCost - 1)).STOP().Done
            : Pay(Prepare.EvmCode, Existing, (UInt256)value).STOP().Done;
        TestState.CreateAccount(TestItem.AddressE, 100);
        TestState.InsertCode(TestItem.AddressE, Pay(Prepare.EvmCode, Existing, (UInt256)value).STOP().Done, Spec);

        BlockAccessListAtIndex bal = ExecuteWithBal(code);

        AccountChangesAtIndex? target = bal.GetAccountChanges(Existing);
        if (outOfGasBeforeAccess)
        {
            Assert.That(target, Is.Null);
            return;
        }

        Assert.That(target, Is.Not.Null);
        Assert.That(target!.BalanceChange?.Value, balanceChanges ? Is.EqualTo((UInt256)12) : Is.Null);
    }

    [Test]
    public void Pay_to_delegated_target_leaves_the_delegation_address_out_of_the_block_access_list()
    {
        if (!amsterdam) Assert.Ignore("EIP-7928 is only enabled on the Amsterdam fixture");

        Address codeHolder = TestItem.AddressE;
        TestState.CreateAccount(Existing, 5);
        TestState.CreateAccount(codeHolder, 0);
        TestState.InsertCode(codeHolder, Prepare.EvmCode.STOP().Done, Spec);
        byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. codeHolder.Bytes];
        TestState.InsertCode(Existing, delegation, Spec);

        BlockAccessListAtIndex bal = ExecuteWithBal(Pay(Prepare.EvmCode, Existing, 7).STOP().Done);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bal.GetAccountChanges(Existing)?.BalanceChange?.Value, Is.EqualTo((UInt256)12));
            Assert.That(bal.HasAccount(codeHolder), Is.False);
        }
    }

    private UInt256 StorageAt(Address address)
    {
        TestState.Get(new StorageCell(address, 0), out UInt256 value);
        return value;
    }

    private BlockAccessListAtIndex ExecuteWithBal(byte[] code)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, GasLimit, code);
        TracedAccessWorldState tracedState = new(TestState, parallel: false);
        tracedState.SetGeneratingBlockAccessList(new BlockAccessListAtIndex());
        EthereumVirtualMachine machine = new(new TestBlockhashProvider(SpecProvider), SpecProvider, LimboLogs.Instance);
        TransactionProcessor<EthereumGasPolicy> processor = new(
            BlobBaseFeeCalculator.Instance, SpecProvider, tracedState, machine, new EthereumCodeInfoRepository(tracedState), LimboLogs.Instance);

        TransactionResult txResult = processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), NullTxTracer.Instance);

        Assert.That(txResult.TransactionExecuted, Is.True, txResult.ToString());
        return tracedState.GetGeneratingBlockAccessList()!;
    }

    // Under EIP-7708 the transaction's own value transfer to the contract is logged first.
    private LogEntry[] ExpectedLogs(params LogEntry[] payLogs) =>
        amsterdam ? [ExpectedTransferLog(Sender, Recipient, 1), .. payLogs] : [];

    private static LogEntry ExpectedTransferLog(Address from, Address to, UInt256 value) =>
        new(TransferLog.Sender, value.ToBigEndian(), [TransferLog.TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]);

    private sealed class LogTracer : TestAllTracerWithOutput
    {
        public LogEntry[] Logs { get; private set; } = [];

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            base.MarkAsSuccess(recipient, in gasSpent, output, logs, stateRoot);
            Logs = logs;
        }
    }
}
