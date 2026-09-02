// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Specs.Forks;
using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Core.Test.Builders.FrameTxTestFrames;

namespace Nethermind.TxPool.Test;

/// <summary>
/// EIP-8141 <c>MAX_PENDING_TXS_USING_NON_CANONICAL_PAYMASTER</c>.
/// </summary>
public class FrameTxPaymasterFilterTests
{
    private static readonly Address Sender = TestItem.AddressA;
    private static readonly Address Paymaster = TestItem.AddressB;
    private static readonly Address OtherPaymaster = TestItem.AddressC;

    [TestCaseSource(nameof(CapCases))]
    public void Accept_CapsOnlyCodeCarryingPaymasters(Func<Transaction> build, bool paymasterHasCode, bool rejected)
    {
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, Unit.Ether);
        if (paymasterHasCode) state.InsertCode([0x60, 0x00], Paymaster);
        else state.CreateAccount(Paymaster, Unit.Ether);

        PendingPaymasterCache cache = new();
        cache.Reserve(Paymaster);

        AcceptTxResult result = Accept(state, cache, build());

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.NonCanonicalPaymasterLimitReached : AcceptTxResult.Accepted));
    }

    private static IEnumerable<TestCaseData> CapCases()
    {
        yield return Case("SponsoredByDeployedPaymaster_Rejected",
            () => FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]), paymasterHasCode: true, rejected: true);

        // The recognized prefix skips a leading expiry and deploy frame, so the cap still keys on the pay target.
        yield return Case("DeployPrefixSponsoredByDeployedPaymaster_Rejected",
            () => FrameTx([ExpiryAt(9999), DeployFrame(), OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]), paymasterHasCode: true, rejected: true);

        // A default-code sponsor is not a paymaster: bounded by the per-payer exposure rule alone.
        yield return Case("SponsoredByDefaultCodeAccount_Accepted",
            () => FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]), paymasterHasCode: false, rejected: false);

        yield return Case("SelfRelay_Accepted",
            () => FrameTx([SelfVerify(PrefixFrameGas)]), paymasterHasCode: true, rejected: false);

        // A null pay target resolves to the sender, so no paymaster is used.
        yield return Case("PayFrameWithoutTarget_Accepted",
            () => FrameTx([OnlyVerify(PrefixFrameGas), Pay(null, PrefixFrameGas)]), paymasterHasCode: true, rejected: false);

        // The leading frame already approves payment for the sender, so the later pay frame sponsors nothing.
        yield return Case("SelfPaidBeforePayFrame_Accepted",
            () => FrameTx([SelfVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]), paymasterHasCode: true, rejected: false);

        // The simulator walks the whole leading VERIFY run, so an unapproving frame before the pay frame
        // must not hide the sponsor from the cap.
        yield return Case("PayFrameBehindSpacerVerifyFrame_Rejected",
            () => FrameTx([OnlyVerify(PrefixFrameGas), SpacerVerifyFrame(), Pay(Paymaster, PrefixFrameGas)]), paymasterHasCode: true, rejected: true);

        yield return Case("NonFrameTx_Accepted",
            () => Build.A.Transaction.WithSenderAddress(Sender).TestObject, paymasterHasCode: true, rejected: false);
    }

    // The processor resolves a pay frame's target as target ?? sender, so naming the sender and omitting it
    // are the same transaction and the cap must not tell them apart. Sender carries code here because an
    // EIP-7702 delegated EOA does, which is what made self-payment look like a non-canonical paymaster.
    [TestCase(true, TestName = "Sender named explicitly in the pay frame")]
    [TestCase(false, TestName = "Pay frame target omitted")]
    public void Accept_CodeCarryingSenderPayingItself_IsNotAPaymaster(bool targetSpelledOut)
    {
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Sender);
        PendingPaymasterCache cache = new();
        cache.Reserve(Sender);

        AcceptTxResult result = Accept(state, cache, FrameTx([OnlyVerify(PrefixFrameGas), Pay(targetSpelledOut ? Sender : null, PrefixFrameGas)], nonce: 1));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    // The self-relay prefix approves payment for the sender, so walking to the first approving frame must
    // not read it as a sponsor whichever way its target is encoded.
    [TestCase(true, TestName = "Self-relay frame naming the sender")]
    [TestCase(false, TestName = "Self-relay frame with no target")]
    public void Accept_SelfRelayPrefix_IsNotAPaymaster(bool targetSpelledOut)
    {
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Sender);
        PendingPaymasterCache cache = new();
        cache.Reserve(Sender);

        TxFrame selfRelay = new(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, targetSpelledOut ? Sender : null, gasLimit: 100_000, UInt256.Zero, default);
        AcceptTxResult result = Accept(state, cache, FrameTx([selfRelay], nonce: 1));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_FirstPendingTxAdmitted_SecondRejected()
    {
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Paymaster);
        PendingPaymasterCache cache = new();
        Transaction tx = FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]);

        // Admission counts the slot itself, so the second submission sees the first one holding it.
        AcceptTxResult first = Accept(state, cache, tx);
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
        cache.Reserve(Paymaster);
        cache.Decrement(Paymaster);

        AcceptTxResult result = Accept(state, cache, FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)]));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [TestCaseSource(nameof(ReplacementCases))]
    public void Accept_DiscountsOnlyTheTxAReplacementDisplaces(ulong pendingNonce, Address pendingPaymaster, ulong incomingNonce, bool rejected)
    {
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Paymaster);
        state.InsertCode([0x60, 0x00], OtherPaymaster);

        Transaction pending = FrameTx([OnlyVerify(PrefixFrameGas), Pay(pendingPaymaster, PrefixFrameGas)], pendingNonce);
        PendingPaymasterCache cache = new();
        cache.Reserve(pendingPaymaster);
        // The incoming tx's own paymaster must be at the cap for the discount to be what decides.
        if (pendingPaymaster != Paymaster) cache.Reserve(Paymaster);

        // A fee bump is the same sender and nonce at a higher price.
        Transaction incoming = FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)], incomingNonce, gasPrice: 2);
        AcceptTxResult result = Accept(state, cache, incoming, Pool(blobs: false, pending));

        Assert.That(result, Is.EqualTo(rejected ? AcceptTxResult.NonCanonicalPaymasterLimitReached : AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_FeeBumpOfBlobCarryingSponsoredTx_Accepted()
    {
        // A blob-carrying frame tx is counted from the blob pool, so its replacement must be discounted there.
        TestReadOnlyStateProvider state = new();
        state.InsertCode([0x60, 0x00], Paymaster);

        Transaction pending = FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)], carriesBlobs: true);
        PendingPaymasterCache cache = new();
        cache.Reserve(Paymaster);

        Transaction incoming = FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)], gasPrice: 2, carriesBlobs: true);
        AcceptTxResult result = Accept(state, cache, incoming, Pool(blobs: true, pending));

        Assert.That(result, Is.EqualTo(AcceptTxResult.Accepted));
    }

    [Test]
    public void Accept_ResolvesThePaymasterOfAPendingLightRecord()
    {
        // The shipped blob mode holds a frameless LightTransaction, and the pool walks those records, so
        // the cap has to key off the record rather than a frame list it no longer has.
        TestReadOnlyStateProvider state = new();
        state.CreateAccount(Sender, Unit.Ether);
        state.InsertCode([0x60, 0x00], Paymaster);

        Transaction record = new LightTransaction(FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)], carriesBlobs: true));
        Assert.That(record.Frames, Is.Null, "the record must be frameless, or this pins nothing");

        PendingPaymasterCache cache = new();
        cache.Reserve(Paymaster);

        // The bump displaces the record, so the discount has to resolve the record's paymaster; a later
        // nonce displaces nothing and must still be capped.
        Transaction bump = FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)], gasPrice: 2, carriesBlobs: true);
        AcceptTxResult replacement = Accept(state, cache, bump, Pool(blobs: true, record));
        AcceptTxResult second = Accept(state, cache, FrameTx([OnlyVerify(PrefixFrameGas), Pay(Paymaster, PrefixFrameGas)], nonce: 1, carriesBlobs: true), Pool(blobs: true, record));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(FrameTxValidation.GetPrefixPaymaster(record), Is.EqualTo(Paymaster));
            Assert.That(replacement, Is.EqualTo(AcceptTxResult.Accepted), "the displaced record frees its sponsor's slot");
            Assert.That(second, Is.EqualTo(AcceptTxResult.NonCanonicalPaymasterLimitReached));
        }
    }

    private static IEnumerable<TestCaseData> ReplacementCases()
    {
        yield return new TestCaseData(0ul, Paymaster, 0ul, false)
            .SetName("Accept_FeeBumpOfSponsoredTx_Accepted");

        // Replacing a tx sponsored elsewhere frees that paymaster's slot, not this one's.
        yield return new TestCaseData(0ul, OtherPaymaster, 0ul, true)
            .SetName("Accept_ReplacementNamingAnotherPaymaster_Rejected");

        // A different nonce joins the pending set rather than displacing it.
        yield return new TestCaseData(0ul, Paymaster, 1ul, true)
            .SetName("Accept_SecondNonceFromSameSender_Rejected");
    }

    [Test]
    public void PendingPaymasterCache_CountsUpAndClampsAtZero()
    {
        PendingPaymasterCache cache = new();

        cache.Reserve(Paymaster);
        cache.Reserve(Paymaster);
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

    private static AcceptTxResult Accept(TestReadOnlyStateProvider state, PendingPaymasterCache cache, Transaction tx, TxDistinctSortedPool? pool = null)
    {
        // The displaced tx sits in whichever pool matches its shape, so both are wired as TxPool does.
        (TxDistinctSortedPool standard, TxDistinctSortedPool blob) = tx.CarriesBlobs
            ? (Pool(blobs: false), pool ?? Pool(blobs: true))
            : (pool ?? Pool(blobs: false), Pool(blobs: true));
        FrameTxPaymasterFilter filter = new(state, standard, blob, cache, LimboLogs.Instance.GetClassLogger<FrameTxPaymasterFilterTests>());
        TxFilteringState filteringState = new(tx, Substitute.For<IAccountStateProvider>(), Eip8141Prototype.Instance);
        return filter.Accept(tx, ref filteringState, TxHandlingOptions.None);
    }

    /// <summary>The real pool type for the shape, so the visitor's ascending-nonce exit is exercised as wired.</summary>
    private static TxDistinctSortedPool Pool(bool blobs, params Transaction[] pending)
    {
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec { IsEip1559Enabled = false });
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(0).TestObject);

        IComparer<Transaction> comparer = new TransactionComparerProvider(specProvider, blockTree).GetDefaultComparer();
        TxDistinctSortedPool pool = blobs
            ? new BlobTxDistinctSortedPool(pending.Length + 1, comparer, LimboLogs.Instance)
            : new TxDistinctSortedPool(pending.Length + 1, comparer, LimboLogs.Instance);
        foreach (Transaction tx in pending)
        {
            pool.TryInsert(tx.Hash!, tx);
        }

        return pool;
    }

    private static Transaction FrameTx(TxFrame[] frames, ulong nonce = 0, uint gasPrice = 1, bool carriesBlobs = false)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Sender,
            Nonce = nonce,
            Frames = frames,
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = gasPrice,
            DecodedMaxFeePerGas = gasPrice,
            BlobVersionedHashes = carriesBlobs ? [new byte[32]] : null,
            MaxFeePerBlobGas = carriesBlobs ? UInt256.One : UInt256.Zero,
        };
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    /// <remarks>A non-approving VERIFY frame, so the paymaster walk has to step over it to reach the PAY frame.</remarks>
    private static TxFrame SpacerVerifyFrame() =>
        new(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, target: TestItem.AddressD, gasLimit: PrefixFrameGas, UInt256.Zero, default);

    private static TxFrame DeployFrame() =>
        new(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, target: null, gasLimit: 50_000, UInt256.Zero, default);
}
