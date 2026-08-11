// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// EIP-8141 <c>MAX_PENDING_TXS_USING_NON_CANONICAL_PAYMASTER</c>: a frame transaction is rejected once
/// the code-carrying target of its <c>pay</c> frame already sponsors the maximum pending transactions.
/// </summary>
public class FrameTxPaymasterFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Paymaster = TestItem.AddressB;

    [TestCaseSource(nameof(CapCases))]
    public void Accept_CapsOnlyCodeCarryingPaymasters(Func<Transaction> build, bool paymasterHasCode, bool rejected)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, Unit.Ether);
        if (paymasterHasCode) state.InsertCode([0x60, 0x00], Paymaster);
        else state.CreateAccount(Paymaster, Unit.Ether);

        PendingPaymasterCache cache = new();
        cache.Increment(Paymaster);

        AcceptTxResult result = Accept(state, cache, build());

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.NonCanonicalPaymasterLimitReached : AcceptTxResult.Accepted));
    }

    private static IEnumerable<TestCaseData> CapCases()
    {
        yield return Case("SponsoredByDeployedPaymaster_Rejected",
            () => FrameTx([OnlyVerifyFrame(), PayFrame(Paymaster)]), paymasterHasCode: true, rejected: true);

        // The recognized prefix skips a leading expiry and deploy frame, so the cap still keys on the pay target.
        yield return Case("DeployPrefixSponsoredByDeployedPaymaster_Rejected",
            () => FrameTx([ExpiryFrame(9999), DeployFrame(), OnlyVerifyFrame(), PayFrame(Paymaster)]), paymasterHasCode: true, rejected: true);

        // A default-code sponsor is not a paymaster: bounded by the per-payer exposure rule alone.
        yield return Case("SponsoredByDefaultCodeAccount_Accepted",
            () => FrameTx([OnlyVerifyFrame(), PayFrame(Paymaster)]), paymasterHasCode: false, rejected: false);

        yield return Case("SelfRelay_Accepted",
            () => FrameTx([SelfVerifyFrame()]), paymasterHasCode: true, rejected: false);

        // A null pay target resolves to the sender, so no paymaster is used.
        yield return Case("PayFrameWithoutTarget_Accepted",
            () => FrameTx([OnlyVerifyFrame(), PayFrame(null)]), paymasterHasCode: true, rejected: false);

        // Not a recognized prefix, so it names no paymaster; the simulation gate decides its fate.
        yield return Case("UnrecognizedPrefix_Accepted",
            () => FrameTx([SelfVerifyFrame(), PayFrame(Paymaster)]), paymasterHasCode: true, rejected: false);

        yield return Case("NonFrameTx_Accepted",
            () => Build.A.Transaction.WithSenderAddress(Sender).TestObject, paymasterHasCode: true, rejected: false);
    }

    [Test]
    public void Accept_FirstPendingTxAdmitted_SecondRejected()
    {
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Paymaster);
        PendingPaymasterCache cache = new();
        Transaction tx = FrameTx([OnlyVerifyFrame(), PayFrame(Paymaster)]);

        AcceptTxResult first = Accept(state, cache, tx);
        // The pool counts an admitted tx on insert, as TxPool does from its Inserted event.
        cache.Increment(Paymaster);
        AcceptTxResult second = Accept(state, cache, tx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(second, Is.EqualTo(AcceptTxResult.NonCanonicalPaymasterLimitReached));
        }
    }

    [Test]
    public void Accept_AfterPendingTxLeavesPool_PaymasterAdmitsAgain()
    {
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Paymaster);
        PendingPaymasterCache cache = new();
        cache.Increment(Paymaster);
        cache.Decrement(Paymaster);

        AcceptTxResult result = Accept(state, cache, FrameTx([OnlyVerifyFrame(), PayFrame(Paymaster)]));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void PendingPaymasterCache_CountsUpAndClampsAtZero()
    {
        PendingPaymasterCache cache = new();

        cache.Increment(Paymaster);
        cache.Increment(Paymaster);
        Assert.That(cache.GetPendingCount(Paymaster), Is.EqualTo(2));

        cache.Decrement(Paymaster);
        Assert.That(cache.GetPendingCount(Paymaster), Is.EqualTo(1));

        // Over-release clamps at zero rather than wrapping, so the cap can never be disabled.
        cache.Decrement(Paymaster);
        cache.Decrement(Paymaster);
        Assert.That(cache.GetPendingCount(Paymaster), Is.EqualTo(0));
    }

    private static TestCaseData Case(string name, Func<Transaction> build, bool paymasterHasCode, bool rejected) =>
        new TestCaseData(build, paymasterHasCode, rejected).SetName($"Accept_{name}");

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PendingPaymasterCache cache, Transaction tx)
    {
        FrameTxPaymasterFilter filter = new(state, cache, LimboLogs.Instance.GetClassLogger<FrameTxPaymasterFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>());
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    private static Transaction FrameTx(TxFrame[] frames) => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = Sender,
        Frames = frames,
    };

    private static TxFrame SelfVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame OnlyVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveExecution, target: null, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame PayFrame(Address? target) =>
        new(TxFrame.ModeVerify, TxFrame.ApprovePayment, target, gasLimit: 100_000, UInt256.Zero, default);

    private static TxFrame DeployFrame() =>
        new(TxFrame.ModeDefault, flags: 0, target: null, gasLimit: 50_000, UInt256.Zero, default);

    private static TxFrame ExpiryFrame(ulong deadline)
    {
        byte[] data = new byte[Eip8141Constants.ExpiryDataLength];
        BinaryPrimitives.WriteUInt64BigEndian(data, deadline);
        return new TxFrame(TxFrame.ModeVerify, flags: 0, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 30_000, UInt256.Zero, data);
    }
}
