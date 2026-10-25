// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>Milli-gas metering with every fixed price at 1,000 milli-gas per gas unit matches legacy metering exactly.</summary>
/// <remarks>The remaining milli-gas then stays a multiple of 1,000, so receipts and traces must equal the legacy ones.</remarks>
[TestFixture(false)]
[TestFixture(true)]
public class Eip8053ScaledPricesTests(bool amsterdam) : VirtualMachineTestsBase
{
    private static readonly Address Factory = new("0x8053000000000000000000000000000000000001");

    private VirtualMachine<MilliGasPolicy<ScaledGasPrices>> _scaledMachine = null!;
    private ITransactionProcessor _legacyProcessor = null!;
    private ITransactionProcessor _scaledProcessor = null!;

    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber;
    protected override ulong Timestamp => amsterdam ? MainnetSpecProvider.AmsterdamBlockTimestamp : MainnetSpecProvider.OsakaBlockTimestamp;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        ILogManager logManager = GetLogManager();
        _scaledMachine = new VirtualMachine<MilliGasPolicy<ScaledGasPrices>>(new TestBlockhashProvider(SpecProvider), SpecProvider, logManager);
        _legacyProcessor = _processor;
        _scaledProcessor = new TransactionProcessor<MilliGasPolicy<ScaledGasPrices>>(BlobBaseFeeCalculator.Instance, SpecProvider, TestState, _scaledMachine, CodeInfoRepository, logManager);

        // Creates a contract with 1,450 bytes of code. With the account charge of the CREATE, the state gas is more than
        // the EIP-8037 allowance of a 300,000-gas call covers.
        TestState.CreateAccount(Factory, 0);
        TestState.InsertCode(Factory, Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(new byte[1450]).Done, 0).Op(Instruction.STOP).Done, SpecProvider.GenesisSpec);
        TestState.Commit(SpecProvider.GenesisSpec);
    }

    [TearDown]
    public override void TearDown()
    {
        _scaledMachine?.Dispose();
        base.TearDown();
    }

    private static IEnumerable<TestCaseData> Programs()
    {
        yield return Program("compute loop", 100_000,
        [
            (byte)Instruction.PUSH2, 0x03, 0xE8,
            (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH1, 1, (byte)Instruction.SWAP1, (byte)Instruction.SUB,
            (byte)Instruction.DUP1, (byte)Instruction.PUSH1, 3, (byte)Instruction.JUMPI,
            (byte)Instruction.STOP,
        ]);
        yield return Program("compute loop out of gas", 30_000,
        [
            (byte)Instruction.JUMPDEST, (byte)Instruction.PUSH1, 0, (byte)Instruction.JUMP,
        ]);
        yield return Program("new slot", 1_000_000,
        [
            (byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE, (byte)Instruction.STOP,
        ]);
        yield return Program("set and clear a slot", 1_000_000,
        [
            (byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE,
            (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE,
            (byte)Instruction.STOP,
        ]);
        // 100 words: 3 × 100 + 100² // 512 = 319 gas. A cost formula with a division is evaluated in gas and then scaled,
        // so this costs 319,000 milli-gas, not the 319,531 of evaluating it in milli-gas.
        yield return Program("memory expansion to 100 words", 100_000,
            Prepare.EvmCode.PushData(1).PushData(99 * 32).Op(Instruction.MSTORE).Op(Instruction.GAS).Op(Instruction.STOP).Done);
        yield return Program("log and memory", 1_000_000,
            Prepare.EvmCode.StoreDataInMemory(64, "c0de").Log(96, 0, [TestItem.KeccakA]).Op(Instruction.GAS).Op(Instruction.STOP).Done);
        yield return Program("value call to a new account", 1_000_000,
            Prepare.EvmCode.CallWithValue(TestItem.AddressF, 50_000, 1).Op(Instruction.STOP).Done);
        yield return Program("call forwarding all gas", 1_000_000,
            Prepare.EvmCode.Call(TestItem.AddressE, 10_000_000).Op(Instruction.GAS).Op(Instruction.STOP).Done);
        yield return Program("create", 1_000_000,
            Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf([(byte)Instruction.STOP]).Done, 0).Op(Instruction.STOP).Done);
        yield return Program("create reverting after a store", 1_000_000,
            Prepare.EvmCode.Create(
                [(byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.REVERT],
                0).Op(Instruction.STOP).Done);
        yield return Program("create halting after a store", 1_000_000,
            Prepare.EvmCode.Create(
                [(byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH0, (byte)Instruction.SSTORE, (byte)Instruction.INVALID],
                0).Op(Instruction.STOP).Done);
        yield return Program("deposit beyond the allowance of a call", 1_000_000,
            Prepare.EvmCode.Call(Factory, 300_000).Op(Instruction.STOP).Done);
        yield return Program("create running out of gas for the deposit", 60_000,
            Prepare.EvmCode.Create(Prepare.EvmCode.ForInitOf(new byte[200]).Done, 0).Op(Instruction.STOP).Done);
    }

    private static TestCaseData Program(string name, ulong gasLimit, byte[] code) => new TestCaseData(gasLimit, code).SetArgDisplayNames(name);

    /// <remarks>
    /// Under EIP-8037 each program runs with an empty state gas reservoir and again with 20M more gas, which goes to the
    /// reservoir.
    /// </remarks>
    [TestCaseSource(nameof(Programs))]
    public void Receipt_and_trace_match_legacy_metering(ulong gasLimit, byte[] code)
    {
        AssertMatchesLegacy(gasLimit, code);
        if (amsterdam)
            AssertMatchesLegacy(gasLimit + 20_000_000, code);
    }

    private void AssertMatchesLegacy(ulong gasLimit, byte[] code)
    {
        (Eip8053ReceiptTracer legacy, GethLikeTxTrace? legacyTrace) = Run(_legacyProcessor, gasLimit, code, traceInstructions: true);
        (Eip8053ReceiptTracer scaled, GethLikeTxTrace? scaledTrace) = Run(_scaledProcessor, gasLimit, code, traceInstructions: true);
        (Eip8053ReceiptTracer scaledUntraced, _) = Run(_scaledProcessor, gasLimit, code, traceInstructions: false);

        using (Assert.EnterMultipleScope())
        {
            foreach (Eip8053ReceiptTracer receipt in new[] { scaled, scaledUntraced })
            {
                Assert.That(receipt.StatusCode, Is.EqualTo(legacy.StatusCode));
                Assert.That(receipt.Gas, Is.EqualTo(legacy.Gas));
                Assert.That(receipt.Output, Is.EqualTo(legacy.Output));
            }

            Assert.That(scaledTrace!.Entries.Select(static entry => (entry.Opcode, entry.Gas, entry.GasCost)),
                Is.EqualTo(legacyTrace!.Entries.Select(static entry => (entry.Opcode, entry.Gas, entry.GasCost))));
        }
    }

    private (Eip8053ReceiptTracer Receipt, GethLikeTxTrace? Trace) Run(ITransactionProcessor processor, ulong gasLimit, byte[] code, bool traceInstructions)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code, value: 0, blockGasLimit: 60_000_000);
        Eip8053ReceiptTracer receipt = new();
        GethLikeTxMemoryTracer? instructions = traceInstructions ? new(transaction, GethTraceOptions.Default) : null;
        processor.CallAndRestore(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)),
            instructions is null ? receipt : new CompositeTxTracer(receipt, instructions));
        return (receipt, instructions?.BuildResult());
    }
}
