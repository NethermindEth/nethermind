// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Int256;
using NUnit.Framework;
using SignedInt256 = Nethermind.Int256.Int256;

namespace Nethermind.Eez.Test.RollupX;

public class NativeValueTests
{
    [Test]
    public void Funding_IncludesRolledBackCallsAndFailedRows()
    {
        CrossChainCall first = new(2, false, 0, Address.Zero, 0, Address.Zero, 5, []);
        CrossChainCall second = first with { RevertNextNCalls = 0, Value = 7 };
        ExpectedCall row = new(default, [second with { Value = 13 }], default, false, []);
        L2ExecutionEntry entry = new(default, [first, second, second with { Value = 11 }], [row], default, true, []);

        Assert.That(NativeValue.Funding([entry]), Is.EqualTo((UInt256)36), "delivery funding includes every recorded value");
    }

    [Test]
    public void Delta_FailedOutboundRoot_HasNoCommittedTransfer()
    {
        NativeCallTree tree = Tree(1, Call(0, 7), Result(false));

        Assert.That(NativeValue.Delta(tree), Is.EqualTo(SignedInt256.Zero), "the failed proxy rolls back its value transfer");
    }

    [Test]
    public void Delta_FailedNestedCall_HasNoCommittedTransfer([Values] bool inbound)
    {
        NativeCallTree tree = Tree(inbound ? 0UL : 1UL, Call(inbound ? 1UL : 0UL, 11), Call(inbound ? 0UL : 1UL, 7), Result(false), Result(true));

        Assert.That(NativeValue.Delta(tree), Is.EqualTo(new SignedInt256(inbound ? 11 : -11)));
    }

    [Test]
    public void Delta_NestedSuccessfulCalls_NetsBothDirections()
    {
        NativeCallTree tree = Tree(0, Call(1, 11), Call(0, 7), Call(1, 3), Result(true), Result(true), Result(true));

        Assert.That(NativeValue.Delta(tree), Is.EqualTo(new SignedInt256(7)));
    }

    [Test]
    public void Delta_RevertedSiblingRegion_DropsAllDescendantTransfers()
    {
        NativeCallTree tree = Tree(1, new StreamMessage.Snapshot(), Call(0, 11), Call(1, 3), Result(true), Result(true),
            Call(0, 7), Result(true), new StreamMessage.Revert(), Call(0, 5), Result(true));

        Assert.That(NativeValue.Delta(tree), Is.EqualTo(new SignedInt256(-5)), "both directions in the reverted region disappear");
    }

    [Test]
    public void Delta_NestedRevertedRegion_PreservesEnclosingTransfer()
    {
        NativeCallTree tree = Tree(0, Call(1, 11), new StreamMessage.Snapshot(), Call(0, 7), Call(1, 3), Result(true), Result(true),
            new StreamMessage.Revert(), Result(true));

        Assert.That(NativeValue.Delta(tree), Is.EqualTo(new SignedInt256(11)));
    }

    [Test]
    public void Delta_SelectedInboundSubtree_DoesNotIncludeOtherDeliveries()
    {
        NativeCallTree tree = Tree(0, Call(1, 11), Result(true), Call(1, 7), Call(0, 3), Result(true), Result(true));

        Assert.That(NativeValue.Delta(tree, tree.Calls[tree.Roots[1]]), Is.EqualTo(new SignedInt256(4)));
    }

    [Test]
    public void Delta_StaticCallbacks_HaveNoValue()
    {
        NativeCallTree tree = Tree(0, new StreamMessage.StaticCall(1, Address.Zero, Address.Zero, 0, ReadOnlyMemory<byte>.Empty),
            new StreamMessage.StaticCall(0, Address.Zero, Address.Zero, 0, ReadOnlyMemory<byte>.Empty), Result(true), Result(true));

        Assert.That(NativeValue.Delta(tree), Is.EqualTo(SignedInt256.Zero));
    }

    [Test]
    public void Delta_Int192Boundaries_AcceptsFullSignedRange()
    {
        UInt256 magnitude = UInt256.One << 191;
        SignedInt256.Subtract(SignedInt256.Zero, new SignedInt256(magnitude), out SignedInt256 minimum);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(NativeValue.Delta(Tree(0, Call(1, magnitude - 1), Result(true))), Is.EqualTo(new SignedInt256(magnitude - 1)));
            Assert.That(NativeValue.Delta(Tree(1, Call(0, magnitude), Result(true))), Is.EqualTo(minimum));
        }
    }

    [Test]
    public void Delta_OutsideInt192Range_RejectsBothDirections()
    {
        UInt256 magnitude = UInt256.One << 191;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => NativeValue.Delta(Tree(0, Call(1, magnitude), Result(true))), Throws.TypeOf<EezSettlementException>());
            Assert.That(() => NativeValue.Delta(Tree(1, Call(0, magnitude + 1), Result(true))), Throws.TypeOf<EezSettlementException>());
        }
    }

    [Test]
    public void Delta_Int256Overflow_RejectsBeforeNetting()
    {
        UInt256 overflow = UInt256.One << 255;
        NativeCallTree tree = Tree(0, Call(1, overflow), Call(0, overflow), Result(true), Result(true));

        Assert.That(() => NativeValue.Delta(tree), Throws.TypeOf<EezSettlementException>(), "equal flows cannot hide an invalid signed intermediate");
    }

    [Test]
    public void Funding_UInt256Overflow_Rejects()
    {
        CrossChainCall call = new(0, false, 0, Address.Zero, 0, Address.Zero, UInt256.MaxValue, []);
        L2ExecutionEntry entry = new(default, [call, call with { Value = 1 }], [], default, true, []);

        Assert.That(() => NativeValue.Funding([entry]), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void Delta_CommittedUInt256Overflow_Rejects()
    {
        NativeCallTree tree = Tree(0, Call(1, UInt256.MaxValue), Result(true), Call(1, 1), Result(true));

        Assert.That(() => NativeValue.Delta(tree), Throws.TypeOf<EezSettlementException>());
    }

    [Test]
    public void Delta_OverflowInsideRevertedRegion_Rejects()
    {
        NativeCallTree tree = Tree(0, new StreamMessage.Snapshot(), Call(1, UInt256.MaxValue), Result(true), Call(1, 1), Result(true), new StreamMessage.Revert());

        Assert.That(() => NativeValue.Delta(tree), Throws.TypeOf<EezSettlementException>(), "rollback cannot hide overflow while accumulating the original calls");
    }

    private static StreamMessage.Call Call(ulong destination, UInt256 value) => new(destination, Address.Zero, Address.Zero, value, 0, ReadOnlyMemory<byte>.Empty);

    private static StreamMessage.Return Result(bool success) => new(success, ReadOnlyMemory<byte>.Empty);

    private static NativeCallTree Tree(ulong origin, params StreamMessage[] calls)
    {
        StreamMessage[] messages = [.. calls, new StreamMessage.Finish()];
        int index = 0;
        return NativeCallTree.Read(messages, ref index, origin, 1);
    }
}
