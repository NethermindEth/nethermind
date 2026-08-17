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
internal class MisplacedExpiryFrameFilterTest
{
    private static Transaction FrameTx(params TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = frames,
        FrameSignatures = [],
    };

    private static TxFrame Expiry() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, Eip8141Constants.ExpiryVerifierAddress, 30_000, UInt256.Zero, new byte[Eip8141Constants.ExpiryDataLength]);

    private static TxFrame SelfVerify() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, 50_000, UInt256.Zero, default);

    private static TxFrame UserOp() =>
        new(TxFrame.ModeSender, TxFrame.ApproveScopeNone, TestItem.AddressB, 1_000, UInt256.Zero, default);

    // EIP-8141 "Expiry Verifier Frame": an expiry verifier frame may appear only as the first frame.
    private static IEnumerable<TestCaseData> PlacementCases()
    {
        yield return new TestCaseData(new[] { SelfVerify(), UserOp() }, AcceptTxResult.Accepted)
            .SetName("a transaction with no expiry frame is admissible");
        yield return new TestCaseData(new[] { Expiry(), SelfVerify(), UserOp() }, AcceptTxResult.Accepted)
            .SetName("a leading expiry frame is admissible");
        yield return new TestCaseData(new[] { SelfVerify(), Expiry() }, AcceptTxResult.FrameTxMisplacedExpiryFrame)
            .SetName("an expiry frame behind the leading frame is rejected");
        // Not reachable through the rule-8 filter: this layout matches no recognized prefix, so placement
        // is the only rule that rejects it.
        yield return new TestCaseData(new[] { UserOp(), Expiry(), SelfVerify() }, AcceptTxResult.FrameTxMisplacedExpiryFrame)
            .SetName("an expiry frame inside an unrecognized layout is rejected");
    }

    [TestCaseSource(nameof(PlacementCases))]
    public void Accept_RejectsAnExpiryFrameThatDoesNotLeadTheFrameList(TxFrame[] frames, AcceptTxResult expected)
    {
        Transaction tx = FrameTx(frames);
        MisplacedExpiryFrameFilter filter = new(LimboLogs.Instance.GetClassLogger<MisplacedExpiryFrameFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(expected));
    }

    [Test]
    public void Accept_LeavesANonFrameTransactionAlone()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithSenderAddress(TestItem.AddressA).TestObject;
        MisplacedExpiryFrameFilter filter = new(LimboLogs.Instance.GetClassLogger<MisplacedExpiryFrameFilterTest>());
        TxFilteringState state = new(tx, Substitute.For<IAccountStateProvider>());

        Assert.That(filter.Accept(tx, ref state, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
    }
}
