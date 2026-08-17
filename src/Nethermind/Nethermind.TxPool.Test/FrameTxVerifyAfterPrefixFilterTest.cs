// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
internal class FrameTxVerifyAfterPrefixFilterTest
{
    private static Transaction FrameTx(params TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = frames,
        FrameSignatures = [],
    };

    private static TxFrame SelfVerify() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, 1_000, UInt256.Zero, default);

    private static TxFrame OnlyVerify() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, 1_000, UInt256.Zero, default);

    private static TxFrame Pay() =>
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, TestItem.AddressC, 1_000, UInt256.Zero, default);

    private static TxFrame Deploy() =>
        new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressD, 1_000, UInt256.Zero, default);

    private static TxFrame UserOp() =>
        new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, 1_000, UInt256.Zero, default);

    private static TxFrame PostOp() =>
        new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, TestItem.AddressB, 1_000, UInt256.Zero, default);

    // EIP-8141 "Structural Rules" rule 8: no VERIFY frame may follow the validation prefix, because its
    // revert would invalidate a pooled transaction on state the pool never validated.
    private static IEnumerable<TestCaseData> Rule8Cases()
    {
        yield return new TestCaseData(new[] { SelfVerify(), UserOp() }, AcceptTxResult.Accepted)
            .SetName("a self relay prefix followed by a user op is admissible");
        yield return new TestCaseData(new[] { Deploy(), OnlyVerify(), Pay(), UserOp(), PostOp() }, AcceptTxResult.Accepted)
            .SetName("the longest recognized prefix followed by a body is admissible");
        yield return new TestCaseData(new[] { SelfVerify(), UserOp(), OnlyVerify() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a VERIFY frame behind a self relay prefix is rejected");
        yield return new TestCaseData(new[] { SelfVerify(), Pay() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a pay frame behind a self relay prefix is rejected");
        yield return new TestCaseData(new[] { OnlyVerify(), Pay(), UserOp(), Pay() }, AcceptTxResult.FrameTxVerifyAfterPrefix)
            .SetName("a VERIFY frame behind a paymaster prefix is rejected");
        // A layout matching none of the four recognized prefixes has no boundary for rule 8 to apply to;
        // the rules that reject it do so on their own terms.
        yield return new TestCaseData(new[] { UserOp(), SelfVerify() }, AcceptTxResult.Accepted)
            .SetName("an unrecognized layout is left to the other rules");
    }

    [TestCaseSource(nameof(Rule8Cases))]
    public void Accept_RejectsAVerifyFrameAfterTheValidationPrefix(TxFrame[] frames, AcceptTxResult expected)
    {
        Transaction tx = FrameTx(frames);
        FrameTxVerifyAfterPrefixFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxVerifyAfterPrefixFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(expected));
    }

    [Test]
    public void Accept_LeavesANonFrameTransactionAlone()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithSenderAddress(TestItem.AddressA).TestObject;
        FrameTxVerifyAfterPrefixFilter filter = new(LimboLogs.Instance.GetClassLogger<FrameTxVerifyAfterPrefixFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
    }
}
