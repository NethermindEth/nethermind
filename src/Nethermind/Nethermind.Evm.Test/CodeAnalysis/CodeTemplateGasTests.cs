// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

/// <summary>
/// Pins the gas a recognized template reports against what the dispatch loop actually charges for the
/// same opcodes, so a fast path built on these numbers cannot silently diverge from the interpreter.
/// </summary>
[TestFixture]
public class CodeTemplateGasTests : VirtualMachineTestsBase
{
    protected override ForkActivation Activation => MainnetSpecProvider.CancunActivation;

    private static readonly Address Implementation = TestItem.AddressC;

    /// <summary>
    /// CALLDATACOPY of calldata carrying only a selector: the opcode's base cost, one word copied, and
    /// the expansion of empty memory to one word.
    /// </summary>
    private const ulong SelectorOnlyCallDataCopyCost = GasCostOf.VeryLow * 2 + GasCostOf.Memory;

    /// <summary>RETURNDATACOPY of nothing still costs the opcode's base, with no word or expansion gas.</summary>
    private const ulong EmptyReturnDataCopyCost = GasCostOf.VeryLow;

    [TestCase(0u, false, false)]
    [TestCase(1u, false, false)]
    [TestCase(2u, false, false)]
    [TestCase(0u, true, false, TestName = "Body opens with its own call value guard")]
    [TestCase(2u, true, false, TestName = "Guarded body further down the chain")]
    [TestCase(0u, false, true, TestName = "Preamble pushes its zeroes with PUSH0")]
    [TestCase(2u, false, true, TestName = "PUSH0 preamble, selector further down the chain")]
    [TestCase(1u, true, true, TestName = "PUSH0 preamble and a per-function guard")]
    public void Dispatcher_reports_the_gas_the_interpreter_charges_to_reach_the_body(uint index, bool perFunctionGuard, bool push0)
    {
        uint[] selectors = [0xa9059cbb, 0x70a08231, 0x18160ddd];
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch(
            selectors, withCallValueGuard: !perFunctionGuard, perFunctionCallValueGuard: perFunctionGuard, push0: push0);
        uint selector = selectors[index];

        SelectorDispatch dispatch = new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch!;
        Assert.That(dispatch.TryResolve(selector, hasCallValue: false, out int bodyProgramCounter, out ulong reportedGas), Is.True);

        GethLikeTxTrace trace = TraceCall(dispatcher.Code, SelectorBytes(selector));

        ulong gasAtEntry = GasAt(trace, programCounter: 0);
        ulong gasAtBody = GasAt(trace, bodyProgramCounter);
        Assert.That(gasAtEntry - gasAtBody, Is.EqualTo(reportedGas));
    }

    [TestCase(ProxyVariant.Eip1167, 31, 44, TestName = "Canonical EIP-1167 runtime")]
    [TestCase(ProxyVariant.Age, 32, 43, TestName = "The shorter 0age runtime")]
    [TestCase(ProxyVariant.Erc7511, 30, 43, TestName = "ERC-7511 PUSH0 runtime")]
    [TestCase(ProxyVariant.Solady, 30, 44, TestName = "Solady PUSH0 runtime")]
    public void Minimal_proxy_reports_the_gas_the_interpreter_charges_around_the_delegate_call(
        ProxyVariant variant, int delegateCallProgramCounter, int returnProgramCounter)
    {
        byte[] implementation = Prepare.EvmCode.Op(Instruction.STOP).Done;
        TestState.CreateAccount(Implementation, UInt256.Zero);
        TestState.InsertCode(Implementation, implementation, SpecProvider.GenesisSpec);
        byte[] code = ProxyCode(variant, Implementation);

        MinimalProxy proxy = new CodeInfo(code).PrepareAnalysis().MinimalProxy!;
        Assert.That(proxy.DelegateCallProgramCounter, Is.EqualTo(delegateCallProgramCounter));

        GethLikeTxTrace trace = TraceCall(code, SelectorBytes(0xa9059cbb));

        // The implementation stops without returning data, so the RETURNDATACOPY costs neither
        // word nor expansion gas and memory never grows past the calldata copy.
        ulong beforeCall = GasAt(trace, programCounter: 0) - GasAt(trace, delegateCallProgramCounter);
        Assert.That(beforeCall, Is.EqualTo(proxy.GasBeforeCall + SelectorOnlyCallDataCopyCost));

        // Measured up to the RETURN, which is itself free once memory has been charged, so the whole of
        // the success tail is covered even for the variants that rebuild the RETURN's operands first.
        ulong afterCall = GasAt(trace, delegateCallProgramCounter + 1) - GasAt(trace, returnProgramCounter);
        Assert.That(afterCall, Is.EqualTo(proxy.GasAfterCall + EmptyReturnDataCopyCost + proxy.GasOnSuccess));
    }

    /// <summary>Which forwarder runtime a case exercises.</summary>
    public enum ProxyVariant { Eip1167, Age, Erc7511, Solady }

    private static byte[] ProxyCode(ProxyVariant variant, Address target) => variant switch
    {
        ProxyVariant.Eip1167 => TemplateCode.MinimalProxy(target),
        ProxyVariant.Age => TemplateCode.AgeMinimalProxy(target),
        ProxyVariant.Erc7511 => TemplateCode.Erc7511MinimalProxy(target),
        ProxyVariant.Solady => TemplateCode.SoladyMinimalProxy(target),
        _ => throw new System.ArgumentOutOfRangeException(nameof(variant), variant, "Unknown forwarder."),
    };


    private static byte[] SelectorBytes(uint selector) =>
        [(byte)(selector >> 24), (byte)(selector >> 16), (byte)(selector >> 8), (byte)selector];

    /// <summary>Gas remaining in the top-level frame immediately before the opcode at <paramref name="programCounter"/>.</summary>
    private static ulong GasAt(GethLikeTxTrace trace, int programCounter)
    {
        foreach (GethTxTraceEntry entry in trace.Entries)
        {
            if (entry.Depth == 1 && entry.ProgramCounter == programCounter) return entry.Gas;
        }

        throw new AssertionException($"The trace has no top-level entry at program counter {programCounter}.");
    }

    private GethLikeTxTrace TraceCall(byte[] code, byte[] input)
    {
        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code, input, UInt256.Zero);
        GethLikeTxMemoryTracer tracer = new(transaction, GethTraceOptions.Default);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
        return tracer.BuildResult();
    }
}
