// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[Parallelizable(ParallelScope.All)]
internal class FrameTxVerifyGasFilterTest
{
    // A frame that may approve both scopes but runs EIP-8141 default code unless the sender has
    // code, followed by a frame that only stays inside the ceiling while the walk stops early.
    private static Transaction FrameTx() => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames =
        [
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 1_000, UInt256.Zero, default),
            new TxFrame(TxFrame.ModeDefault, TxFrame.ApproveScopeNone, target: TestItem.AddressB, gasLimit: 3_000_000, UInt256.Zero, default),
        ],
        FrameSignatures = [],
    };

    // The account readers diverge on the out-value of a failed lookup, and a zero code hash reads
    // back as code-bearing. Scoring a missing sender that way ends the prefix walk at a frame that
    // provably cannot approve, so the frames behind it are never counted against MAX_VERIFY_GAS.
    [TestCase(false, TestName = "a missing sender is codeless, so the whole prefix is counted")]
    [TestCase(true, TestName = "a code-bearing sender ends the prefix at the approving frame")]
    public void Accept_ScoresAMissingSenderAsCodeless(bool senderExistsWithCode)
    {
        IAccountStateProvider accounts = Substitute.For<IAccountStateProvider>();
        if (senderExistsWithCode)
        {
            accounts.TryGetAccount(TestItem.AddressA, out Arg.Any<AccountStruct>()).Returns(call =>
            {
                call[1] = new AccountStruct(0, UInt256.One, Keccak.EmptyTreeHash.ValueHash256, TestItem.KeccakA.ValueHash256);
                return true;
            });
        }

        FrameTxVerifyGasFilter filter = new(new TxPoolConfig { FrameTxMaxVerifyGas = 100_000 }, LimboLogs.Instance.GetClassLogger<FrameTxVerifyGasFilterTest>());
        Transaction tx = FrameTx();
        TxFilteringState state = new(tx, accounts);

        AcceptTxResult result = filter.Accept(tx, ref state, TxHandlingOptions.None);

        Assert.That(result, Is.EqualTo(senderExistsWithCode ? AcceptTxResult.Accepted : AcceptTxResult.FrameTxVerifyGasTooHigh));
    }

    // The pool's account cache stores the empty account on a miss while the reader beneath it may
    // leave the out-value zeroed, so a filter reading the first probe and a filter reading the
    // second one must not see a different sender.
    [Test]
    public void SenderAccount_OfAMissingAccount_ReadsTheSameOnEveryProbe()
    {
        TxFilteringState state = new(FrameTx(), Substitute.For<IAccountStateProvider>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.SenderAccount.HasCode, Is.False);
            Assert.That(state.SenderAccount.IsTotallyEmpty, Is.True);
            Assert.That(state.SenderAccount.HasCode, Is.False);
        }
    }
}
