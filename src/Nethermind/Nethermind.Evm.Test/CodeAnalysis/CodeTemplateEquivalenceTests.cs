// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

/// <summary>
/// Runs the same call twice — once with instruction tracing, which forces the dispatch loop, and once
/// without, which takes a template fast path — and requires the two to agree.
/// </summary>
/// <remarks>
/// Gas, status and return data are the whole caller-visible result of a frame, so agreement across both
/// paths is what keeps the fast paths from being a consensus change. Each run starts from a freshly
/// built world state, because executing a transaction leaves balances and nonces behind that would
/// otherwise make the second run's gas incomparable.
/// </remarks>
[TestFixture]
public class CodeTemplateEquivalenceTests : VirtualMachineTestsBase
{
    protected override ForkActivation Activation => MainnetSpecProvider.CancunActivation;

    private static readonly Address Implementation = TestItem.AddressC;
    private static readonly Address InnerImplementation = TestItem.AddressD;
    private static readonly uint[] Selectors = [0xa9059cbb, 0x70a08231, 0x18160ddd];

    /// <summary>Enough selectors, spread widely enough, to give the binary search several pivot levels.</summary>
    private static readonly uint[] TreeSelectors =
        [0x18160ddd, 0x23b872dd, 0x313ce567, 0x70a08231, 0x95d89b41, 0xa9059cbb, 0xdd62ed3e, 0xf2fde38b, 0x06fdde03];

    [TestCase(0xa9059cbbu, TestName = "First selector in the chain")]
    [TestCase(0x70a08231u, TestName = "Selector in the middle of the chain")]
    [TestCase(0x18160dddu, TestName = "Last selector in the chain")]
    [TestCase(0xdeadbeefu, TestName = "Selector that falls through to the fallback")]
    public void Matches_the_dispatch_loop_for_a_guarded_dispatcher(uint selector) =>
        AssertBothPathsAgree(Nothing, Dispatcher(withCallValueGuard: true), SelectorBytes(selector), UInt256.Zero);

    [TestCase(0xa9059cbbu, TestName = "First selector in the chain")]
    [TestCase(0xdeadbeefu, TestName = "Selector that falls through to the fallback")]
    public void Matches_the_dispatch_loop_for_an_unguarded_dispatcher(uint selector) =>
        AssertBothPathsAgree(Nothing, Dispatcher(withCallValueGuard: false), SelectorBytes(selector), UInt256.Zero);

    [Test]
    public void Matches_the_dispatch_loop_when_the_guard_rejects_the_call_value() =>
        AssertBothPathsAgree(Nothing, Dispatcher(withCallValueGuard: true), SelectorBytes(Selectors[0]), UInt256.One);

    [Test]
    public void Matches_the_dispatch_loop_when_a_value_bearing_call_is_unguarded() =>
        AssertBothPathsAgree(Nothing, Dispatcher(withCallValueGuard: false), SelectorBytes(Selectors[0]), UInt256.One);

    [TestCase(0, TestName = "Empty calldata")]
    [TestCase(3, TestName = "Calldata shorter than a selector")]
    public void Matches_the_dispatch_loop_for_calldata_below_a_selector(int length) =>
        AssertBothPathsAgree(Nothing, Dispatcher(withCallValueGuard: true), SelectorBytes(Selectors[0])[..length], UInt256.Zero);

    /// <summary>
    /// Every selector of a binary-search dispatcher, so each distinct path through the tree — and the
    /// gas the fast path claims for it — is checked against the dispatch loop.
    /// </summary>
    [Test]
    public void Matches_the_dispatch_loop_for_every_path_through_a_binary_search_tree()
    {
        uint[] selectors = TreeSelectors;
        byte[] code = TemplateCode.SelectorDispatch(selectors, withCallValueGuard: true, DispatchShape.BinarySearch).Code;

        foreach (uint selector in selectors)
        {
            AssertBothPathsAgree(Nothing, code, SelectorBytes(selector), UInt256.Zero);
        }
    }

    [Test]
    public void Matches_the_dispatch_loop_when_a_binary_search_tree_falls_through_to_its_fallback() =>
        AssertBothPathsAgree(
            Nothing,
            TemplateCode.SelectorDispatch(TreeSelectors, withCallValueGuard: true, DispatchShape.BinarySearch).Code,
            SelectorBytes(0xdeadbeef),
            UInt256.Zero);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_returns_data() =>
        AssertProxyPathsAgree(Prepare.EvmCode.StoreDataInMemory(0, TestItem.KeccakA.BytesToArray()).Return(32, 0).Done);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_reverts() =>
        AssertProxyPathsAgree(Prepare.EvmCode.StoreDataInMemory(0, TestItem.KeccakA.BytesToArray()).Revert(32, 0).Done);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_stops_without_output() =>
        AssertProxyPathsAgree(Prepare.EvmCode.Op(Instruction.STOP).Done);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_runs_out_of_gas() =>
        AssertProxyPathsAgree(Prepare.EvmCode.Op(Instruction.JUMPDEST).PushData(0).Op(Instruction.JUMP).Done);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_writes_storage() =>
        AssertProxyPathsAgree(Prepare.EvmCode.PersistData("0x01", "0x02").Op(Instruction.STOP).Done);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_has_no_code() =>
        AssertBothPathsAgree(Nothing, TemplateCode.MinimalProxy(Implementation), new byte[4], UInt256.Zero);

    [Test]
    public void Matches_the_dispatch_loop_when_the_proxy_target_is_a_precompile() =>
        AssertBothPathsAgree(Nothing, TemplateCode.MinimalProxy(Address.FromNumber(4)), new byte[64], UInt256.Zero);

    [TestCase(0, TestName = "Empty calldata")]
    [TestCase(31, TestName = "Calldata below one word")]
    [TestCase(256, TestName = "Calldata spanning several words")]
    public void Matches_the_dispatch_loop_across_calldata_sizes(int length) =>
        AssertProxyPathsAgree(Prepare.EvmCode.Op(Instruction.STOP).Done, new byte[length]);

    [Test]
    public void Matches_the_dispatch_loop_when_a_proxy_forwards_to_another_proxy() =>
        AssertBothPathsAgree(
            () =>
            {
                Deploy(InnerImplementation, Prepare.EvmCode.Op(Instruction.STOP).Done);
                Deploy(Implementation, TemplateCode.MinimalProxy(InnerImplementation));
            },
            TemplateCode.MinimalProxy(Implementation), new byte[4], UInt256.Zero);

    [Test]
    public void Matches_the_dispatch_loop_when_a_proxy_forwards_into_a_dispatcher() =>
        AssertProxyPathsAgree(Dispatcher(withCallValueGuard: true), SelectorBytes(Selectors[1]));

    private static void Nothing() { }

    private void AssertProxyPathsAgree(byte[] implementation, byte[]? input = null) =>
        AssertBothPathsAgree(
            () => Deploy(Implementation, implementation),
            TemplateCode.MinimalProxy(Implementation),
            input ?? new byte[4],
            UInt256.Zero);

    private void Deploy(Address address, byte[] code)
    {
        TestState.CreateAccount(address, UInt256.Zero);
        TestState.InsertCode(address, code, SpecProvider.GenesisSpec);
    }

    private static byte[] Dispatcher(bool withCallValueGuard) =>
        TemplateCode.SelectorDispatch(Selectors, withCallValueGuard).Code;

    private static byte[] SelectorBytes(uint selector) =>
        [(byte)(selector >> 24), (byte)(selector >> 16), (byte)(selector >> 8), (byte)selector];

    private void AssertBothPathsAgree(Action deploy, byte[] code, byte[] input, UInt256 value)
    {
        CallOutputTracer fastPath = new();
        RunFromFreshState(deploy, code, input, value, fastPath);

        InstructionTracingCallOutputTracer dispatchLoop = new();
        RunFromFreshState(deploy, code, input, value, dispatchLoop);

        Assert.Multiple(() =>
        {
            Assert.That(fastPath.GasSpent, Is.EqualTo(dispatchLoop.GasSpent), "gas spent");
            Assert.That(fastPath.StatusCode, Is.EqualTo(dispatchLoop.StatusCode), "status code");
            Assert.That(fastPath.ReturnValue, Is.EqualTo(dispatchLoop.ReturnValue), "return value");
            Assert.That(fastPath.Error, Is.EqualTo(dispatchLoop.Error), "error");
        });
    }

    private void RunFromFreshState(Action deploy, byte[] code, byte[] input, UInt256 value, ITxTracer tracer)
    {
        TearDown();
        Setup();
        deploy();

        (Block block, Transaction transaction) = PrepareTx(Activation, 100_000UL, code, input, value);
        _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
    }

    /// <summary>Records the same result as <see cref="CallOutputTracer"/> while forcing the dispatch loop.</summary>
    private sealed class InstructionTracingCallOutputTracer : CallOutputTracer
    {
        public override bool IsTracingInstructions => true;
    }
}
