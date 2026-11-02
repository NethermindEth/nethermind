// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Filters;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class DependencyProofTxFilterTests
{
    [Test]
    public void Sender_quota_rejection_releases_filter_leases_and_can_be_retried_after_removal()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        // Records are bounded by MAX_WRAPPER_BYTES, so the sender quota is reached after a few near-maximum ones.
        byte[] proof = new byte[LeanProofStore.MaxWrapperBytes - 1024];
        int pinned = (int)(LeanProofStore.MaxSenderPinnedBytes / proof.Length);
        Transaction[] transactions = new Transaction[pinned + 1];
        for (int i = 0; i < transactions.Length; i++)
        {
            FrameDependency dependency = Dependency($"dependency {i}");
            store.AddVerified([dependency], null, proof);
            transactions[i] = Transaction(dependency);
        }
        Transaction first = transactions[0], candidate = transactions[^1];
        for (int i = 0; i < pinned; i++) Assert.That(store.PinPending(transactions[i]), Is.True);
        DependencyProofTxFilter coverage = new(store);
        DependencyProofSenderQuotaTxFilter quota = new(store);
        TxFilteringState filtering = new(candidate, Substitute.For<IAccountStateProvider>(), Eip8288Prototype.Instance);
        try
        {
            Assert.That(coverage.Accept(candidate, ref filtering, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(quota.Accept(candidate, ref filtering, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.MissingDependencyProof));
            store.UnpinPending(first.Hash!.ValueHash256);
            Assert.That(quota.Accept(candidate, ref filtering, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(store.PinPending(candidate), Is.True);
        }
        finally
        {
            filtering.ProofReservation?.Dispose();
            filtering.SenderProofReservation?.Dispose();
        }
        Assert.That(store.Covers(candidate), Is.True);

        static FrameDependency Dependency(string name) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(name), default);
        static Transaction Transaction(FrameDependency dependency) => new()
        {
            Type = TxType.FrameTx,
            SenderAddress = Address.Zero,
            Hash = new Hash256(dependency.DataHash),
            NonceKeys = [UInt256.Zero],
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
    }
}
