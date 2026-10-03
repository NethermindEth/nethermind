// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>One case per EIP-8369 Profile 2 candidate condition, each failing on its own so that no case
/// would still pass if a different condition were the one being checked.</summary>
public class Eip8369Profile2Tests
{
    private const ulong Budget = Eip8369Constants.MaxVerifyGasPerTx;

    [TestCaseSource(nameof(CandidacyCases))]
    public Profile2Exclusion Classify_CandidateConditionMatrix(Action<Transaction> mutate, ulong maxVerifyGasPerTx)
        => Eip8369Profile2.Classify(FrameTx(mutate), maxVerifyGasPerTx);

    private static IEnumerable<TestCaseData> CandidacyCases()
    {
        // Condition 2: the four admitted prefix shapes.
        yield return Case("SelfVerify_IsCandidate",
            static tx => tx.Frames = [SelfVerify()], Profile2Exclusion.None);
        yield return Case("DeployThenSelfVerify_IsCandidate",
            static tx => tx.Frames = [Deploy(), SelfVerify()], Profile2Exclusion.None);
        yield return Case("OnlyVerifyThenPay_IsCandidate",
            static tx => tx.Frames = [OnlyVerify(), Pay()], Profile2Exclusion.None);
        yield return Case("DeployThenOnlyVerifyThenPay_IsCandidate",
            static tx => tx.Frames = [Deploy(), OnlyVerify(), Pay()], Profile2Exclusion.None);
        // A body frame behind the prefix is outside the prefix, so it does not affect the shape.
        yield return Case("SelfVerifyThenBodyFrame_IsCandidate",
            static tx => tx.Frames = [SelfVerify(), Body()], Profile2Exclusion.None);

        // Shape matching ignores the optional expiry verifier frame.
        yield return Case("ExpiryThenSelfVerify_IsCandidate",
            static tx => tx.Frames = [Expiry(), SelfVerify()], Profile2Exclusion.None);
        yield return Case("ExpiryThenDeployThenOnlyVerifyThenPay_IsCandidate",
            static tx => tx.Frames = [Expiry(), Deploy(), OnlyVerify(), Pay()], Profile2Exclusion.None);
        // Only a leading expiry frame is stepped over; behind the prefix it is an ordinary VERIFY frame.
        yield return Case("SelfVerifyThenExpiry_VerifyFrameAfterPrefix",
            static tx => tx.Frames = [SelfVerify(), Expiry()], Profile2Exclusion.VerifyFrameAfterPrefix);

        // Condition 2 failures.
        yield return Case("NoFrames_UnrecognizedPrefix",
            static tx => tx.Frames = null, Profile2Exclusion.UnrecognizedPrefix);
        yield return Case("BodyFrameOnly_UnrecognizedPrefix",
            static tx => tx.Frames = [Body()], Profile2Exclusion.UnrecognizedPrefix);
        // An execution-only approval that no payment frame follows nominates no payer.
        yield return Case("OnlyVerifyWithoutPay_UnrecognizedPrefix",
            static tx => tx.Frames = [OnlyVerify(), Body()], Profile2Exclusion.UnrecognizedPrefix);
        // The prefix VERIFY frame must target the sender.
        yield return Case("VerifyTargetingThirdParty_UnrecognizedPrefix",
            static tx => tx.Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, TestItem.AddressB, 50_000, UInt256.Zero, default)],
            Profile2Exclusion.UnrecognizedPrefix);

        // Condition 3, batch half: a prefix frame carrying ATOMIC_BATCH_FLAG matches no admitted shape.
        yield return Case("AtomicBatchOnDeployFrame_UnrecognizedPrefix",
            static tx => tx.Frames = [Deploy(FrameFlags.AtomicBatch), SelfVerify(), Body()], Profile2Exclusion.UnrecognizedPrefix);
        yield return Case("AtomicBatchOnPayFrame_UnrecognizedPrefix",
            static tx => tx.Frames = [OnlyVerify(), Pay(FrameFlags.ApprovePayment | FrameFlags.AtomicBatch), Body()],
            Profile2Exclusion.UnrecognizedPrefix);

        // Condition 3, VERIFY half.
        yield return Case("VerifyFrameBehindTheSelfVerifyPrefix_VerifyFrameAfterPrefix",
            static tx => tx.Frames = [SelfVerify(), Body(), Verify()], Profile2Exclusion.VerifyFrameAfterPrefix);
        yield return Case("VerifyFrameBehindThePayPrefix_VerifyFrameAfterPrefix",
            static tx => tx.Frames = [OnlyVerify(), Pay(), Verify()], Profile2Exclusion.VerifyFrameAfterPrefix);

        // Condition 4: the budget spans the prefix's declared limits plus signature verification.
        yield return Case("PrefixWithinBudget_IsCandidate",
            static tx => tx.Frames = [SelfVerify(gasLimit: Budget)], Profile2Exclusion.None);
        yield return Case("PrefixOverBudget_VerifyBudgetExceeded",
            static tx => tx.Frames = [SelfVerify(gasLimit: Budget + 1)], Profile2Exclusion.VerifyBudgetExceeded);
        // limits.state counts as a declared limit alongside limits.execution.
        yield return Case("PrefixStateGasOverBudget_VerifyBudgetExceeded",
            static tx => tx.Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, Budget, 1, UInt256.Zero, default)],
            Profile2Exclusion.VerifyBudgetExceeded);
        // A body frame behind the prefix is not part of the budget, however large.
        yield return Case("BodyFrameOutsideTheBudget_IsCandidate",
            static tx => tx.Frames = [SelfVerify(gasLimit: Budget), Body(gasLimit: Budget)], Profile2Exclusion.None);
        // The expiry frame is stepped over for shape matching but still counts toward the budget.
        yield return Case("ExpiryFrameCountsTowardTheBudget_VerifyBudgetExceeded",
            static tx => tx.Frames = [Expiry(gasLimit: 1), SelfVerify(gasLimit: Budget)], Profile2Exclusion.VerifyBudgetExceeded);
        yield return Case("SignatureVerificationCountsTowardTheBudget_VerifyBudgetExceeded",
            static tx =>
            {
                tx.Frames = [SelfVerify(gasLimit: Budget - Eip8141Constants.Secp256k1VerificationGasCost + 1)];
                tx.FrameSignatures = [Secp256k1Signature()];
            },
            Profile2Exclusion.VerifyBudgetExceeded);
        // Saturating arithmetic: a prefix that would overflow the sum must not wrap under the cap.
        yield return Case("PrefixGasSaturates_VerifyBudgetExceeded",
            static tx => tx.Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, ulong.MaxValue, ulong.MaxValue, UInt256.Zero, default)],
            Profile2Exclusion.VerifyBudgetExceeded);
        // A zero cap lifts the limit, as ITxPoolConfig.FrameTxMaxVerifyGas does.
        yield return Case("ZeroCapLiftsTheBudget_IsCandidate",
            static tx => tx.Frames = [SelfVerify(gasLimit: ulong.MaxValue)], Profile2Exclusion.None, maxVerifyGasPerTx: 0);

        // Outside both profiles: blob gas has its own budget, over which EIP-8369 defines no omission check.
        yield return Case("BlobCarryingFrameTx_CarriesBlobs",
            static tx => tx.BlobVersionedHashes = [new byte[32]], Profile2Exclusion.CarriesBlobs);
        // The blob check comes first: a blob carrier is outside the profiles whatever its shape.
        yield return Case("BlobCarryingBodyOnlyFrameTx_CarriesBlobs",
            static tx =>
            {
                tx.Frames = [Body()];
                tx.BlobVersionedHashes = [new byte[32]];
            },
            Profile2Exclusion.CarriesBlobs);
    }

    private static TestCaseData Case(string name, Action<Transaction> mutate, Profile2Exclusion expected, ulong maxVerifyGasPerTx = Budget) =>
        new(mutate, maxVerifyGasPerTx) { TestName = name, ExpectedResult = expected };

    private static Transaction FrameTx(Action<Transaction> mutate)
    {
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [SelfVerify()],
            FrameSignatures = [],
        };
        mutate(tx);
        return tx;
    }

    private static TxFrame SelfVerify(ulong gasLimit = 50_000) =>
        new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame OnlyVerify(ulong gasLimit = 50_000) =>
        new(FrameMode.Verify, FrameFlags.ApproveExecution, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame Pay(FrameFlags flags = FrameFlags.ApprovePayment, ulong gasLimit = 50_000) =>
        new(FrameMode.Verify, flags, TestItem.AddressB, gasLimit, UInt256.Zero, default);

    private static TxFrame Deploy(FrameFlags flags = FrameFlags.None, ulong gasLimit = 50_000) =>
        new(FrameMode.Default, flags, target: null, gasLimit, UInt256.Zero, default);

    private static TxFrame Body(ulong gasLimit = 50_000) =>
        new(FrameMode.Default, FrameFlags.None, TestItem.AddressC, gasLimit, UInt256.Zero, default);

    private static TxFrame Verify(ulong gasLimit = 50_000) =>
        new(FrameMode.Verify, FrameFlags.None, TestItem.AddressC, gasLimit, UInt256.Zero, default);

    private static TxFrame Expiry(ulong gasLimit = 30_000) =>
        new(FrameMode.Verify, FrameFlags.None, Eip8141Constants.ExpiryVerifierAddress, gasLimit, UInt256.Zero, new byte[Eip8141Constants.ExpiryDataLength]);

    private static TxFrameSignature Secp256k1Signature() =>
        new(TxFrameSignature.SchemeSecp256k1, TestItem.AddressA, default, new byte[65]);
}
