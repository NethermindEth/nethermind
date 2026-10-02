// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Eip8288;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Eip8288;

public class ProofWrapperServiceTests
{
    [Test]
    public async Task Oversized_dependency_frames_are_rejected_before_proof_verification([Values] bool inclusionList)
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(0, 0);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        byte[] data = Eip8288Dependencies.Serialize(Enumerable.Repeat(dependency, Eip8288Constants.MaxDependenciesPerFrame + 1).ToArray());
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null,
                (ulong)(Eip8288Constants.MaxDependenciesPerFrame + 1) * Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)]
        };
        RecursiveStark recursive = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));
        Result<Hash256[]> accepted = inclusionList
            ? await service.AcceptInclusionListAsync(FocilInclusionListDecoder.Instance.Encode(new FocilInclusionList
            {
                Transactions = [transaction],
                RecursiveStark = recursive
            }).Bytes)
            : await service.AcceptAsync(MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = [new WrapperTransaction(transaction)],
                Deps = [dependency],
                Mode = MempoolWrapper.ModeRecursive,
                RecursiveStark = recursive
            }).Bytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.IsSuccess, Is.False);
            Assert.That(accepted.Error, Is.EqualTo(FrameTxValidation.TooManyDependenciesPerFrame));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public void Shared_dependencies_cannot_create_unbounded_transaction_payloads()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(10, 256 * 1024);
        byte[] first = service.BuildWrapper().Data!;
        MempoolWrapper decoded = Decode(first);
        byte[] second = service.BuildWrapper().Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Transactions, Has.Count.EqualTo(7));
            Assert.That(first.Length, Is.LessThan(LeanProofStore.MaxWrapperBytes - Eip8288Constants.MaxProofBytes));
            Assert.That(ValueKeccak.Compute(second), Is.Not.EqualTo(ValueKeccak.Compute(first)));
            Assert.That(decoded.Transactions.Concat(Decode(second).Transactions).Select(transaction => transaction.Full!.Hash).Distinct().Count(), Is.EqualTo(10));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Rotation_does_not_reprove_an_unchanged_complete_selection()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(3, 64);
        byte[] first = service.BuildWrapper().Data!;
        byte[] second = service.BuildWrapper().Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValueKeccak.Compute(second), Is.EqualTo(ValueKeccak.Compute(first)));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Large_discarded_parent_coverage_is_skipped_before_proving()
    {
        LeanProofStore store = new();
        Transaction[] transactions = new Transaction[2];
        for (int i = 0; i < transactions.Length; i++)
        {
            FrameDependency[] parent = Enumerable.Range(0, 2200).Select(index => new FrameDependency(
                Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"{i}:{index}"), default)).ToArray();
            store.AddVerified(parent, null, [(byte)i]);
            transactions[i] = new Transaction
            {
                Type = TxType.FrameTx,
                ChainId = 1,
                SenderAddress = Address.Zero,
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([parent[0]]))]
            };
            transactions[i].Hash = transactions[i].CalculateHash();
        }
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService(transactions, store, verifier);
        byte[] first = service.BuildWrapper().Data!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Decode(first).Transactions, Has.Count.EqualTo(1));
            Assert.That(verifier.LargestRecursiveInput, Is.EqualTo(1));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Valid_proof_pool_rejection_has_a_distinct_status()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        ITxPool pool = Substitute.For<ITxPool>();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
            call.Arg<Transaction>().Nonce == 0 ? AcceptTxResult.FeeTooLow : AcceptTxResult.Accepted);
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        LeanProofStore store = new();
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, new FakeLeanProofVerifier(true));
        Transaction later = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Nonce = 1,
            Frames = transaction.Frames
        };
        byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction), new WrapperTransaction(later)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        ProofWrapperAcceptance result = await service.AcceptDetailedAsync(encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.PoolRejected));
            Assert.That(result.HasValidProof, Is.True);
            Assert.That(result.Result.Error, Is.EqualTo(AcceptTxResult.FeeTooLow.ToString()));
            Assert.That(store.Covers(transaction), Is.True);
            pool.Received(1).SubmitTx(Arg.Is<Transaction>(entry => entry.Nonce == 1), TxHandlingOptions.PersistentBroadcast);
            Assert.That((await service.AcceptDetailedAsync([0])).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Invalid));
            Assert.That((await service.AcceptAsync(encoded)).IsSuccess, Is.False);
        }
    }

    [Test]
    public void Rpc_aggregation_is_rate_limited_and_cooperatively_cancelled()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(1, 0);
        Assert.That(service.BuildWrapper(rateLimit: true).IsSuccess, Is.True);
        Assert.That(service.BuildWrapper(rateLimit: true).Error, Does.Contain("rate limited"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.BuildWrapper(cancellationToken: cancellation.Token));
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Rejected_wrappers_do_not_retain_proofs_and_repeats_reuse_verification([Values] bool proofValid)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        ITxPool pool = Substitute.For<ITxPool>();
        LeanProofStore store = new();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            Assert.That(store.Covers(call.Arg<Transaction>()), Is.True);
            return AcceptTxResult.FeeTooLow;
        });
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        FakeLeanProofVerifier verifier = new(proofValid);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
        byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        for (int i = 0; i < 3; i++)
            Assert.That((await service.AcceptDetailedAsync(encoded)).Status,
                Is.EqualTo(proofValid ? ProofWrapperAcceptanceStatus.PoolRejected : ProofWrapperAcceptanceStatus.Invalid));
        Assert.That(store.TryGetInput([dependency], out _), Is.False);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
    }

    [Test]
    public void Cancellation_preserves_only_successfully_admitted_coverage([Values] bool inclusionList, [Values] bool firstAccepted)
    {
        using CancellationTokenSource cancellation = new();
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("first"), default);
        FrameDependency second = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("second"), default);
        Transaction[] transactions = new[] { first, second }.Select((dependency, index) => new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Nonce = (ulong)index,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        }).ToArray();
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize([first, second]);
        ITxPool pool = Substitute.For<ITxPool>();
        LeanProofStore store = new();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            Assert.That(store.Covers(call.Arg<Transaction>()), Is.True);
            cancellation.Cancel();
            return firstAccepted ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow;
        });
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, new FakeLeanProofVerifier(true));
        if (inclusionList)
        {
            byte[] encoded = FocilInclusionListDecoder.Instance.Encode(new FocilInclusionList
            {
                Transactions = transactions,
                RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(dependencies)))
            }).Bytes;
            Assert.ThrowsAsync<OperationCanceledException>(async () => { await service.AcceptInclusionListAsync(encoded, cancellation.Token); });
        }
        else
        {
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = transactions.Select(transaction => new WrapperTransaction(transaction)).ToArray(),
                Deps = dependencies,
                Mode = MempoolWrapper.ModeDirect,
                Proofs = [[1], [1]]
            }).Bytes;
            Assert.ThrowsAsync<OperationCanceledException>(async () => { await service.AcceptAsync(encoded, cancellation.Token); });
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Covers(transactions[0]), Is.EqualTo(firstAccepted));
            Assert.That(store.Covers(transactions[1]), Is.False);
            pool.Received(1).SubmitTx(Arg.Is<Transaction>(transaction => transaction.Nonce == 0), TxHandlingOptions.PersistentBroadcast);
            pool.DidNotReceive().SubmitTx(Arg.Is<Transaction>(transaction => transaction.Nonce == 1), Arg.Any<TxHandlingOptions>());
        }
    }

    private static (ProofWrapperService, FakeLeanProofVerifier) Create(int count, int payloadBytes)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        byte[] payload = new byte[payloadBytes];
        Transaction[] transactions = Enumerable.Range(0, count).Select(index => new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Nonce = (ulong)index,
            Frames =
            [
                new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([dependency])),
                new(FrameMode.Sender, FrameFlags.None, Address.Zero, 100_000, UInt256.Zero, payload)
            ]
        }).ToArray();
        foreach (Transaction transaction in transactions) transaction.Hash = transaction.CalculateHash();
        LeanProofStore store = new();
        store.AddVerified([dependency], [[1]], null);
        FakeLeanProofVerifier verifier = new(true);
        return (CreateService(transactions, store, verifier), verifier);
    }

    private static ProofWrapperService CreateService(Transaction[] transactions, LeanProofStore store, FakeLeanProofVerifier verifier)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(transactions);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        return new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
    }

    private static MempoolWrapper Decode(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return MempoolWrapperDecoder.Instance.Decode(ref reader)!;
    }
}
