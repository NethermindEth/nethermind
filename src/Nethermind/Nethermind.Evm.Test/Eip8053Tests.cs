// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using IllustrativeMilliGasPolicy = Nethermind.Evm.GasPolicy.MilliGasPolicy<Nethermind.Evm.GasPolicy.Eip8053IllustrativePrices>;

namespace Nethermind.Evm.Test;

/// <summary>EIP-8053 milli-gas metering with gas units kept observable, against the legacy path on the same fork.</summary>
[TestFixture(true)]
[TestFixture(false)]
public class Eip8053Tests(bool traceInstructions) : VirtualMachineTestsBase
{
    private const ulong LegacyBlock = 1;
    private const ulong MilliGasBlock = 2;
    private const ulong AmsterdamLegacyBlock = 3;
    private const ulong AmsterdamMilliGasBlock = 4;
    private const ulong TxGasLimit = 100_000;

    private static readonly Address Callee = TestItem.AddressC;

    private static readonly ISpecProvider Eip8053SpecProvider = new CustomSpecProvider(
        ((ForkActivation)0, new OverridableReleaseSpec(Osaka.Instance) { IsEip7778Enabled = true }),
        ((ForkActivation)MilliGasBlock, new OverridableReleaseSpec(Osaka.Instance) { IsEip7778Enabled = true, IsEip8053Enabled = true }),
        ((ForkActivation)AmsterdamLegacyBlock, Amsterdam.Instance),
        ((ForkActivation)AmsterdamMilliGasBlock, new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8053Enabled = true }));

    private VirtualMachine<IllustrativeMilliGasPolicy> _milliGasMachine = null!;

    protected override ISpecProvider SpecProvider => Eip8053SpecProvider;
    protected override ulong BlockNumber => MilliGasBlock;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        ILogManager logManager = GetLogManager();
        _milliGasMachine = new VirtualMachine<IllustrativeMilliGasPolicy>(new TestBlockhashProvider(SpecProvider), SpecProvider, logManager);
        TransactionProcessor<IllustrativeMilliGasPolicy> milliGasProcessor = new(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, _milliGasMachine, CodeInfoRepository, logManager);
        _processor = new Eip8053TransactionProcessor(SpecProvider, _processor, milliGasProcessor);
    }

    [TearDown]
    public override void TearDown()
    {
        _milliGasMachine?.Dispose();
        base.TearDown();
    }

    [TestCase(true, 0, 78_998UL)]
    [TestCase(true, 1, 78_997UL)]
    [TestCase(true, 10, 78_997UL)]
    [TestCase(true, 11, 78_996UL)]
    [TestCase(false, 10, 78_988UL)]
    public void Gas_opcode_pushes_remaining_gas_rounded_down(bool eip8053, int jumpDests, ulong expectedGas)
    {
        RunResult result = Run(eip8053, [.. JumpDests(jumpDests), .. ReturnGas]);

        Assert.That(result.OutputWord, Is.EqualTo((UInt256)expectedGas));
    }

    /// <remarks>Under EIP-8053 ten <c>JUMPDEST</c>s cost one gas, and a fraction of a gas unit rounds the transaction up.</remarks>
    [TestCase(true, 0, 21_000UL)]
    [TestCase(true, 1, 21_001UL)]
    [TestCase(true, 10, 21_001UL)]
    [TestCase(true, 11, 21_002UL)]
    [TestCase(false, 1, 21_001UL)]
    [TestCase(false, 10, 21_010UL)]
    public void Transaction_gas_used_rounds_up_to_a_whole_gas_unit(bool eip8053, int jumpDests, ulong expectedGasUsed)
    {
        RunResult result = Run(eip8053, JumpDests(jumpDests));

        Assert.That(result.Gas.SpentGas, Is.EqualTo(expectedGasUsed));
    }

    [Test]
    public void Call_gas_argument_is_in_gas_units([Values] bool eip8053)
    {
        DeployCallee(ReturnGas);

        RunResult result = Run(eip8053, CallCallee(callGas: 50_000, value: 0, returnStatus: false));

        // The callee's GAS charges 2 gas before it reads.
        Assert.That(result.OutputWord, Is.EqualTo((UInt256)49_998));
    }

    /// <remarks>The 63/64 rule works on whole gas units, so the child gets whole gas and the fraction stays with the caller.</remarks>
    [TestCase(6_400_063UL, 6_300UL, 6_300_000UL)]
    [TestCase(6_400_063UL, 6_301UL, 6_300_000UL)]
    [TestCase(100_500UL, 1_000UL, 99_000UL)]
    public void Call_forwards_whole_gas_capped_at_63_64_of_whole_gas_left(ulong availableMilliGas, ulong requestedGas, ulong expectedChildMilliGas)
    {
        IllustrativeMilliGasPolicy gas = IllustrativeMilliGasPolicy.FromULong(availableMilliGas);

        bool reserved = IllustrativeMilliGasPolicy.TryReserveChildGas(ref gas, (UInt256)requestedGas, Osaka.Instance, out ulong childGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reserved, Is.True);
            Assert.That(childGas, Is.EqualTo(expectedChildMilliGas));
            Assert.That(gas.Value, Is.EqualTo(availableMilliGas - expectedChildMilliGas));
        }
    }

    [Test]
    public void Create_forwards_63_64_of_whole_gas_left_and_the_caller_keeps_the_fraction()
    {
        IllustrativeMilliGasPolicy gas = IllustrativeMilliGasPolicy.FromULong(100_500);

        IllustrativeMilliGasPolicy.TryReserveChildGas(ref gas, Osaka.Instance, out ulong childGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(childGas, Is.EqualTo(99_000UL));
            Assert.That(RemainingGasUnits(IllustrativeMilliGasPolicy.FromULong(childGas)), Is.EqualTo(99UL));
            Assert.That(gas.Value, Is.EqualTo(1_500UL));
        }
    }

    /// <remarks>Ten calls whose callee spends 0.1 gas cost one gas in total, so no frame rounds the gas it returns.</remarks>
    [TestCase(true, 1UL)]
    [TestCase(false, 10UL)]
    public void Child_returns_unused_milli_gas_exactly(bool eip8053, ulong expectedExtraGas)
    {
        byte[] tenCalls = Enumerable.Repeat(CallCalleeAndPop, 10).SelectMany(static call => call).ToArray();

        DeployCallee([(byte)Instruction.STOP]);
        ulong withoutJumpDest = Run(eip8053, tenCalls).Gas.SpentGas;
        DeployCallee([(byte)Instruction.JUMPDEST, (byte)Instruction.STOP]);
        ulong withJumpDest = Run(eip8053, tenCalls).Gas.SpentGas;

        Assert.That(withJumpDest - withoutJumpDest, Is.EqualTo(expectedExtraGas));
    }

    [Test]
    public void Value_transfer_gives_the_callee_a_2300_gas_stipend([Values] bool eip8053)
    {
        DeployCallee(ReturnGas);

        RunResult result = Run(eip8053, CallCallee(callGas: 0, value: 1, returnStatus: false));

        Assert.That(result.OutputWord, Is.EqualTo((UInt256)2_298));
    }

    /// <remarks>
    /// The callee spends 3.1 gas (7 gas without EIP-8053) before its no-op SSTORE, which EIP-2200 rejects unless more than
    /// 2,300 gas is left. Under EIP-8053 2,304 gas leaves 2,300.9 gas: the milli-gas comparison passes where one on
    /// whole gas units would not.
    /// </remarks>
    [TestCase(true, 2_303U, (byte)0, StatusCode.Failure)]
    [TestCase(true, 2_304U, (byte)0, StatusCode.Success)]
    [TestCase(true, 0U, (byte)1, StatusCode.Failure)]
    [TestCase(false, 2_307U, (byte)0, StatusCode.Failure)]
    [TestCase(false, 2_308U, (byte)0, StatusCode.Success)]
    [TestCase(false, 0U, (byte)1, StatusCode.Failure)]
    public void Sstore_reentrancy_sentry_compares_milli_gas_with_the_stipend(bool eip8053, uint callGas, byte value, byte expectedCallStatus)
    {
        DeployCallee([(byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.JUMPDEST, (byte)Instruction.SSTORE, (byte)Instruction.STOP]);

        RunResult result = Run(eip8053, CallCallee(callGas, value, returnStatus: true));

        Assert.That(result.OutputWord, Is.EqualTo((UInt256)expectedCallStatus));
    }

    /// <remarks>
    /// The callee runs only SSTORE, SLOAD, LOG0 and CALL with operands from base-cost opcodes, which EIP-8053 keeps at
    /// their prices: it needs exactly 22,699 gas either way. The caller's six repriced opcodes save 9 gas.
    /// </remarks>
    [Test]
    public void Fixed_call_budget_for_state_access_behaves_the_same_with_and_without_eip8053([Values(22_698U, 22_699U)] uint callGas)
    {
        DeployCallee(
        [
            (byte)Instruction.CALLER, (byte)Instruction.CALLER, (byte)Instruction.SSTORE,
            (byte)Instruction.CALLER, (byte)Instruction.SLOAD,
            (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.LOG0,
            (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
            (byte)Instruction.ORIGIN, (byte)Instruction.GAS, (byte)Instruction.CALL,
            (byte)Instruction.STOP,
        ]);
        byte[] caller = CallCallee(callGas, value: 0, returnStatus: true);

        RunResult legacy = Run(eip8053: false, caller);
        RunResult milliGas = Run(eip8053: true, caller);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(legacy.OutputWord, Is.EqualTo((UInt256)(callGas >= 22_699 ? StatusCode.Success : StatusCode.Failure)));
            Assert.That(milliGas.OutputWord, Is.EqualTo(legacy.OutputWord));
            Assert.That(legacy.Gas.SpentGas - milliGas.Gas.SpentGas, Is.EqualTo(9UL));
        }
    }

    /// <remarks>
    /// Each of the 1,000 iterations costs 26 gas without EIP-8053 and 17.6 with it (<c>JUMPDEST</c> 0.1, five very-low
    /// opcodes 1.5 each, <c>JUMPI</c> 10): execution drops to about 68%.
    /// </remarks>
    [TestCase(false, 47_003UL)]
    [TestCase(true, 38_602UL)]
    public void Compute_loop_costs_less(bool eip8053, ulong expectedGasUsed)
    {
        byte[] loop =
        [
            (byte)Instruction.PUSH2, 0x03, 0xE8,
            (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH1, 1, (byte)Instruction.SWAP1, (byte)Instruction.SUB,
            (byte)Instruction.DUP1, (byte)Instruction.PUSH1, 3, (byte)Instruction.JUMPI,
            (byte)Instruction.STOP,
        ];

        RunResult result = Run(eip8053, loop);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(result.Gas.SpentGas, Is.EqualTo(expectedGasUsed));
        }
    }

    /// <remarks>
    /// Setting and clearing a slot earns a 19,900 gas refund, capped at a fifth of the gas used before refunds, which is
    /// rounded up to a whole gas unit first. EIP-7778 block gas is the gas used before refunds.
    /// </remarks>
    [TestCase(false, 43_212UL, 34_570UL)]
    [TestCase(true, 43_206UL, 34_565UL)]
    public void Refund_cap_and_block_gas_use_gas_units(bool eip8053, ulong expectedBlockGas, ulong expectedGasUsed)
    {
        byte[] setAndClear =
        [
            (byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE,
            (byte)Instruction.STOP,
        ];

        RunResult result = Run(eip8053, setAndClear);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Gas.SpentGas, Is.EqualTo(expectedGasUsed));
            Assert.That(result.Gas.BlockGas, Is.EqualTo(expectedBlockGas));
        }
    }

    /// <remarks>100 non-zero calldata bytes set an EIP-7623 floor of 25,000 gas above the 22,600.1 gas the transaction uses.</remarks>
    [Test]
    public void Calldata_floor_applies_to_gas_units([Values] bool eip8053)
    {
        RunResult result = Run(eip8053, [(byte)Instruction.JUMPDEST], input: Enumerable.Repeat((byte)0xFF, 100).ToArray());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Gas.SpentGas, Is.EqualTo(25_000UL));
            Assert.That(result.Gas.BlockGas, Is.EqualTo(25_000UL));
        }
    }

    [Test]
    public void Instruction_trace_reports_remaining_gas_in_gas_units()
    {
        byte[] code = [.. JumpDests(3), (byte)Instruction.GAS, (byte)Instruction.STOP];

        GethLikeTxTrace trace = ExecuteAndTrace(MilliGasBlock, TxGasLimit, code);

        Assert.That(trace.Entries.Select(static entry => entry.Gas), Is.EqualTo(new ulong[] { 79_000, 78_999, 78_999, 78_999, 78_997 }));
    }

    /// <remarks>
    /// EIP-8037: three <c>JUMPDEST</c>s leave 0.7 gas of fraction. The SSTORE then spills all its state gas into
    /// <c>gas_left</c> (the reservoir is empty below the transaction gas cap), and a reverting child returns its spilled
    /// state gas. Both state gas steps must keep the fraction: the legacy run differs only by the repriced opcodes.
    /// </remarks>
    [Test]
    public void State_gas_spill_and_revert_credit_keep_the_milli_gas_fraction()
    {
        DeployCallee([(byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT]);
        byte[] code =
        [
            .. JumpDests(3),
            (byte)Instruction.GAS,
            (byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE,
            (byte)Instruction.GAS,
            (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
            (byte)Instruction.PUSH20, .. Callee.Bytes,
            (byte)Instruction.PUSH3, 0xFF, 0xFF, 0xFF,
            (byte)Instruction.CALL, (byte)Instruction.POP,
            (byte)Instruction.GAS, (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH1, 0x40, (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1, 0x20, (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH0, (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1, 0x60, (byte)Instruction.PUSH0, (byte)Instruction.RETURN,
        ];

        RunResult legacy = Run(AmsterdamLegacyBlock, 1_000_000, code);
        RunResult milliGas = Run(AmsterdamMilliGasBlock, 1_000_000, code);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(milliGas.StatusCode, Is.EqualTo(StatusCode.Success));
            Assert.That(legacy.StatusCode, Is.EqualTo(StatusCode.Success));
            // JUMPDESTs cost 0.3 gas instead of 3, and GAS rounds 2.3 gas up to 3.
            Assert.That(milliGas.Word(0), Is.EqualTo(legacy.Word(0) + 2));
            // Two PUSH1 at 1.5 gas, the SSTORE's cold access and write, its whole state gas spill, then GAS.
            Assert.That(milliGas.Word(0) - milliGas.Word(1),
                Is.EqualTo((UInt256)(3 + Eip8038Constants.ColdStorageAccess + Eip8038Constants.StorageWrite + (ulong)GasCostOf.SSetState + 2)));
            // PUSH20, PUSH3 and POP in the caller and two PUSH1 in the child save 7 gas; the child's spill comes back.
            Assert.That(legacy.Word(1) - legacy.Word(2) - (milliGas.Word(1) - milliGas.Word(2)), Is.EqualTo((UInt256)7));
            // 22.6 gas of repriced opcodes in all, rounded once at the end. The last JUMPDEST leaves a fraction that a
            // dropped 0.7 gas would push over a whole gas unit.
            Assert.That(legacy.Gas.SpentGas - milliGas.Gas.SpentGas, Is.EqualTo(22UL));
            Assert.That(milliGas.Gas.BlockStateGas, Is.EqualTo(legacy.Gas.BlockStateGas));
        }
    }

    private static readonly byte[] ReturnGas =
    [
        (byte)Instruction.GAS, (byte)Instruction.PUSH0, (byte)Instruction.MSTORE,
        (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH0, (byte)Instruction.RETURN,
    ];

    private static readonly byte[] CallCalleeAndPop =
    [
        (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
        (byte)Instruction.PUSH20, .. Callee.Bytes,
        (byte)Instruction.PUSH2, 0xFF, 0xFF,
        (byte)Instruction.CALL, (byte)Instruction.POP,
    ];

    private static ulong RemainingGasUnits<TGasPolicy>(in TGasPolicy gas) where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.GetRemainingGasUnits(in gas);

    private static byte[] JumpDests(int count) => Enumerable.Repeat((byte)Instruction.JUMPDEST, count).ToArray();

    /// <summary>Calls <see cref="Callee"/> with <paramref name="callGas"/> and returns either the call status or the callee's first output word.</summary>
    private static byte[] CallCallee(uint callGas, byte value, bool returnStatus) =>
    [
        (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
        (byte)Instruction.PUSH1, value,
        (byte)Instruction.PUSH20, .. Callee.Bytes,
        (byte)Instruction.PUSH4, (byte)(callGas >> 24), (byte)(callGas >> 16), (byte)(callGas >> 8), (byte)callGas,
        (byte)Instruction.CALL,
        .. returnStatus ? [(byte)Instruction.PUSH0, (byte)Instruction.MSTORE] : new[] { (byte)Instruction.POP },
        (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH0, (byte)Instruction.RETURN,
    ];

    private void DeployCallee(byte[] code)
    {
        if (!TestState.AccountExists(Callee))
            TestState.CreateAccount(Callee, 0);
        TestState.InsertCode(Callee, code, SpecProvider.GenesisSpec);
        TestState.Commit(SpecProvider.GenesisSpec);
    }

    /// <summary>Runs <paramref name="code"/> as the recipient of a transaction and restores the state afterwards.</summary>
    private RunResult Run(bool eip8053, byte[] code, byte[]? input = null) =>
        Run(eip8053 ? MilliGasBlock : LegacyBlock, TxGasLimit, code, input);

    private RunResult Run(ulong blockNumber, ulong gasLimit, byte[] code, byte[]? input = null)
    {
        (Block block, Transaction transaction) = PrepareTx((blockNumber, Timestamp), gasLimit, code, input ?? [], UInt256.Zero);
        Eip8053ReceiptTracer receipt = new();
        ITxTracer tracer = traceInstructions
            ? new CompositeTxTracer(receipt, new GethLikeTxMemoryTracer(transaction, GethTraceOptions.Default))
            : receipt;

        _processor.CallAndRestore(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return new RunResult(receipt.StatusCode, receipt.Gas, receipt.Output);
    }

    private readonly record struct RunResult(byte StatusCode, GasConsumed Gas, byte[] Output)
    {
        public UInt256 OutputWord => Word(0);

        public UInt256 Word(int index) => new(Output.AsSpan(index * 32, 32), isBigEndian: true);
    }
}

/// <summary>Records the receipt-level outcome of a transaction.</summary>
internal sealed class Eip8053ReceiptTracer : TxTracer
{
    public override bool IsTracingReceipt => true;
    public byte StatusCode { get; private set; }
    public GasConsumed Gas { get; private set; }
    public byte[] Output { get; private set; } = [];

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
        (StatusCode, Gas, Output) = (Evm.StatusCode.Success, gasSpent, output);

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
        (StatusCode, Gas, Output) = (Evm.StatusCode.Failure, gasSpent, output);
}
