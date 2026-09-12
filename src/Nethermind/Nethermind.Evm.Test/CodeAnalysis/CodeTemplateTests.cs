// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

public class CodeTemplateTests
{
    private static readonly Address ProxyTarget = TestItem.AddressC;

    [Test]
    public void Recognizes_the_age_minimal_proxy_and_its_target()
    {
        CodeTemplate template = new CodeInfo(TemplateCode.AgeMinimalProxy(ProxyTarget)).PrepareAnalysis();

        Assert.That(template.MinimalProxyTarget, Is.EqualTo(ProxyTarget));
        Assert.That(template.MinimalProxy, Is.Not.Null);
        Assert.That(template.SelectorDispatch, Is.Null);
    }

    [Test]
    public void Recognizes_canonical_minimal_proxy_and_its_target()
    {
        CodeTemplate template = new CodeInfo(TemplateCode.MinimalProxy(ProxyTarget)).PrepareAnalysis();

        Assert.That(template.MinimalProxyTarget, Is.EqualTo(ProxyTarget));
        Assert.That(template.SelectorDispatch, Is.Null);
    }

    [TestCase(0, TestName = "Prefix byte differs")]
    [TestCase(30, TestName = "Suffix byte differs")]
    [TestCase(44, TestName = "Trailing byte differs")]
    public void Rejects_minimal_proxy_with_a_mutated_byte(int index)
    {
        byte[] code = TemplateCode.MinimalProxy(ProxyTarget);
        code[index]++;

        Assert.That(new CodeInfo(code).PrepareAnalysis().MinimalProxyTarget, Is.Null);
    }

    [Test]
    public void Rejects_minimal_proxy_of_the_wrong_length()
    {
        byte[] code = TemplateCode.MinimalProxy(ProxyTarget);

        Assert.That(new CodeInfo(code.Append((byte)0).ToArray()).PrepareAnalysis().MinimalProxyTarget, Is.Null);
        Assert.That(new CodeInfo(code[..^1]).PrepareAnalysis().MinimalProxyTarget, Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Resolves_every_selector_in_the_dispatch_chain(bool withCallValueGuard)
    {
        uint[] selectors = [0xa9059cbb, 0x70a08231, 0x18160ddd];
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch(selectors, withCallValueGuard);

        SelectorDispatch? dispatch = new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch;

        Assert.That(dispatch, Is.Not.Null);
        Assert.That(dispatch!.RejectsCallValue, Is.EqualTo(withCallValueGuard));
        foreach (uint selector in selectors)
        {
            Assert.That(dispatch.TryResolve(selector, hasCallValue: false, out int programCounter, out _), Is.True);
            Assert.That(programCounter, Is.EqualTo(dispatcher.Bodies[selector]));
        }
    }

    [Test]
    public void Declines_a_selector_that_is_not_in_the_chain()
    {
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch([0xa9059cbb], withCallValueGuard: true);

        Assert.That(new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch!.TryResolve(0xdeadbeef, hasCallValue: false, out _, out _), Is.False);
    }

    [Test]
    public void Rejects_a_dispatcher_whose_comparison_jumps_outside_a_jump_destination()
    {
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch([0xa9059cbb], withCallValueGuard: true);
        // Point the only comparison at the PUSH1 data byte that follows the fallback's JUMPDEST.
        byte[] code = dispatcher.Code;
        int targetOffset = Array.IndexOf(code, (byte)Instruction.PUSH4) + 6;
        code[targetOffset + 1] = (byte)(dispatcher.FallbackProgramCounter + 2);

        Assert.That(new CodeInfo(code).PrepareAnalysis().SelectorDispatch, Is.Null);
    }

    [Test]
    public void Rejects_code_that_is_neither_template() =>
        Assert.That(new CodeInfo(Prepare.EvmCode.Op(Instruction.STOP).Done).PrepareAnalysis(), Is.SameAs(CodeTemplate.None));

    [TestCase(3, TestName = "Tree that is a single leaf run")]
    [TestCase(9, TestName = "Tree with several pivot levels")]
    [TestCase(32, TestName = "Tree over a large interface")]
    public void Resolves_every_selector_in_a_binary_search_tree(int count)
    {
        uint[] selectors = Selectors(count);
        TemplateCode.Dispatcher dispatcher =
            TemplateCode.SelectorDispatch(selectors, withCallValueGuard: true, DispatchShape.BinarySearch);

        SelectorDispatch? dispatch = new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch;

        Assert.That(dispatch, Is.Not.Null);
        foreach (uint selector in selectors)
        {
            Assert.That(dispatch!.TryResolve(selector, hasCallValue: false, out int programCounter, out _), Is.True, $"selector {selector:x8}");
            Assert.That(programCounter, Is.EqualTo(dispatcher.Bodies[selector]));
        }
    }

    [TestCase(3, TestName = "Tree that is a single leaf run")]
    [TestCase(9, TestName = "Tree with several pivot levels")]
    [TestCase(32, TestName = "Tree over a large interface")]
    public void Resolves_every_selector_in_a_less_than_pivoted_tree(int count)
    {
        uint[] selectors = Selectors(count);
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch(
            selectors, withCallValueGuard: true, DispatchShape.BinarySearch, lessThanPivots: true);

        SelectorDispatch? dispatch = new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch;

        Assert.That(dispatch, Is.Not.Null);
        foreach (uint selector in selectors)
        {
            Assert.That(dispatch!.TryResolve(selector, hasCallValue: false, out int programCounter, out _), Is.True, $"selector {selector:x8}");
            Assert.That(programCounter, Is.EqualTo(dispatcher.Bodies[selector]));
        }
    }

    /// <summary>
    /// An LT pivot is taken strictly above itself, so a selector equal to the pivot belongs on the
    /// fall-through. A tree claiming otherwise must be rejected rather than resolved to the wrong body.
    /// </summary>
    [Test]
    public void Rejects_a_less_than_tree_that_routes_its_own_pivot_to_the_taken_branch()
    {
        uint[] selectors = Selectors(9);
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch(
            selectors, withCallValueGuard: true, DispatchShape.BinarySearch, lessThanPivots: true);

        // Lower the root pivot to the selector just below it, which leaves that selector sitting in the
        // strictly-above subtree the pivot no longer sends it to.
        byte[] code = dispatcher.Code;
        int rootPivot = Array.IndexOf(code, (byte)Instruction.PUSH4) + 1;
        uint pivot = (uint)((code[rootPivot] << 24) | (code[rootPivot + 1] << 16) | (code[rootPivot + 2] << 8) | code[rootPivot + 3]);
        uint lowered = pivot - 1;
        code[rootPivot] = (byte)(lowered >> 24);
        code[rootPivot + 1] = (byte)(lowered >> 16);
        code[rootPivot + 2] = (byte)(lowered >> 8);
        code[rootPivot + 3] = (byte)lowered;

        Assert.That(new CodeInfo(code).PrepareAnalysis().SelectorDispatch, Is.Null);
    }

    [Test]
    public void Rejects_a_tree_whose_pivot_contradicts_the_subtree_it_routes_to()
    {
        uint[] selectors = Selectors(9);
        TemplateCode.Dispatcher dispatcher =
            TemplateCode.SelectorDispatch(selectors, withCallValueGuard: true, DispatchShape.BinarySearch);

        // Move the root pivot below every selector, so the subtree reached when "pivot > selector" holds
        // can no longer contain only selectors below it.
        byte[] code = dispatcher.Code;
        int rootPivot = Array.IndexOf(code, (byte)Instruction.PUSH4) + 1;
        code[rootPivot] = 0x00;
        code[rootPivot + 1] = 0x00;
        code[rootPivot + 2] = 0x00;
        code[rootPivot + 3] = 0x01;

        Assert.That(new CodeInfo(code).PrepareAnalysis().SelectorDispatch, Is.Null);
    }

    [Test]
    public void Resumes_past_a_function_s_own_call_value_guard()
    {
        uint[] selectors = [0xa9059cbb, 0x70a08231];
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch(
            selectors, withCallValueGuard: false, perFunctionCallValueGuard: true);

        SelectorDispatch? dispatch = new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch;

        Assert.That(dispatch, Is.Not.Null);
        foreach (uint selector in selectors)
        {
            Assert.That(dispatch!.TryResolve(selector, hasCallValue: false, out int programCounter, out _), Is.True);

            // The guard is nine opcodes wide, so resuming past it must land beyond the body's JUMPDEST.
            Assert.That(programCounter, Is.GreaterThan(dispatcher.Bodies[selector] + 1), "resumed inside the guard");
            Assert.That(dispatcher.Code[programCounter], Is.EqualTo((byte)Instruction.MSIZE), "resumed at the body's first opcode");
        }
    }

    [Test]
    public void Declines_a_guarded_function_when_the_call_carries_value()
    {
        TemplateCode.Dispatcher dispatcher = TemplateCode.SelectorDispatch(
            [0xa9059cbb], withCallValueGuard: false, perFunctionCallValueGuard: true);

        SelectorDispatch dispatch = new CodeInfo(dispatcher.Code).PrepareAnalysis().SelectorDispatch!;

        Assert.That(dispatch.TryResolve(0xa9059cbb, hasCallValue: true, out _, out _), Is.False);
        Assert.That(dispatch.TryResolve(0xa9059cbb, hasCallValue: false, out _, out _), Is.True);
    }

    /// <summary>Spreads selectors across the 32-bit space so a binary search over them has real depth.</summary>
    private static uint[] Selectors(int count)
    {
        uint[] selectors = new uint[count];
        for (int i = 0; i < count; i++)
        {
            selectors[i] = 0x10000000u + (uint)i * 0x07654321u;
        }

        return selectors;
    }
}
