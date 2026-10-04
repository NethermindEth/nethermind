// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Core.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.ProofAggregation;

public class ProofWrapperServiceTests
{
    [TestCase("post-tx")]
    [TestCase("keyed")]
    [TestCase("recent-roots")]
    [TestCase("scalar")]
    public async Task Frame_fork_mismatch_is_local_and_can_be_retried_without_a_negative_memo(string scenario)
    {
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance)
        {
            IsEip7906Enabled = scenario != "post-tx",
            IsEip8250Enabled = scenario != "keyed",
            IsEip8272Enabled = scenario != "recent-roots"
        };
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        TxFrame dependencyFrame = new(FrameMode.DepVerify, FrameFlags.None, null,
            Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize([dependency]));
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            NonceKeys = scenario == "scalar" ? null : [UInt256.Zero],
            RecentRootReferences = scenario == "recent-roots" ? [] : null,
            Frames = scenario == "post-tx"
                ? [dependencyFrame, new(FrameMode.PostTx, FrameFlags.None, Address.Zero, 1000, UInt256.Zero, default)]
                : [dependencyFrame]
        };
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService([], new LeanProofStore(), verifier, spec);
        byte[] Encode() => MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        byte[] wrapper = Encode();

        ProofWrapperAcceptance deferred = await service.AcceptDetailedAsync(wrapper);
        Assert.That(deferred.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.LocalFailure));
        Assert.That(verifier.VerificationCalls, Is.Zero);
        spec.IsEip7906Enabled = true;
        spec.IsEip8250Enabled = scenario != "scalar";
        spec.IsEip8272Enabled = true;
        Assert.That((await service.AcceptDetailedAsync(wrapper)).HasValidProof, Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));

        transaction.Frames![0] = new(FrameMode.DepVerify, (FrameFlags)0xff, null,
            Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize([dependency]));
        Assert.That((await service.AcceptDetailedAsync(Encode())).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Invalid));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1), "intrinsic malformed data is rejected before verification");
    }

    [Test]
    public async Task Oversized_dependency_frames_are_rejected_before_proof_verification([Values] bool inclusionList)
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(0, 0);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        byte[] data = Eip8288Dependencies.Serialize(Enumerable.Repeat(dependency, Eip8288Constants.MaxDependenciesPerFrame + 1).ToArray());
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null,
                (ulong)(Eip8288Constants.MaxDependenciesPerFrame + 1) * Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)]
        };
        RecursiveStark recursive = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));
        Result<Hash256[]> accepted = inclusionList
            ? await service.AcceptInclusionListAsync(InclusionListProofPackageDecoder.Instance.Encode(new InclusionListProofPackage
            {
                Transactions = [transaction],
                ProvenDependencies = Eip8288Dependencies.Serialize([dependency]),
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
    public async Task Scalar_nonce_is_rejected_before_proof_verification([Values] bool inclusionList)
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(0, 0);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        RecursiveStark recursive = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));
        Result<Hash256[]> accepted = inclusionList
            ? await service.AcceptInclusionListAsync(InclusionListProofPackageDecoder.Instance.Encode(new InclusionListProofPackage
            {
                Transactions = [transaction],
                ProvenDependencies = Eip8288Dependencies.Serialize([dependency]),
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
            Assert.That(accepted.Error, Is.EqualTo(FrameTxValidation.LegacyNonceNotAllowed));
            Assert.That(verifier.VerificationCalls, Is.Zero);
        }
    }

    [Test]
    public async Task Recent_root_envelopes_before_their_fork_are_rejected_before_proof_verification([Values] bool inclusionList)
    {
        FakeLeanProofVerifier verifier = new(true);
        IReleaseSpec spec = new OverridableReleaseSpec(Eip8288Prototype.Instance) { IsEip8272Enabled = false };
        ProofWrapperService service = CreateService([], new LeanProofStore(), verifier, spec);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            RecentRootReferences = [new(default, 0, default)],
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        RecursiveStark recursive = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));
        Result<Hash256[]> accepted = inclusionList
            ? await service.AcceptInclusionListAsync(InclusionListProofPackageDecoder.Instance.Encode(new InclusionListProofPackage
            {
                Transactions = [transaction],
                ProvenDependencies = Eip8288Dependencies.Serialize([dependency]),
                RecursiveStark = recursive
            }).Bytes)
            : await service.AcceptAsync(MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = [new WrapperTransaction(transaction)],
                Deps = [dependency],
                Mode = MempoolWrapper.ModeRecursive,
                RecursiveStark = recursive
            }).Bytes);
        Assert.That(accepted.Error, Is.EqualTo(FrameTxValidation.RecentRootReferencesNotEnabled));
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    [Test]
    public async Task Pool_eviction_after_preflight_retains_the_verified_verdict_without_peer_penalty()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        transaction.Hash = transaction.CalculateHash();
        ITxPool pool = Substitute.For<ITxPool>();
        bool missingAfterPreflight = true;
        int resolves = 0;
        pool.TryGetPendingTransaction(transaction.Hash.ValueHash256, out Arg.Any<Transaction?>()).Returns(call =>
        {
            bool found = !missingAfterPreflight || ++resolves == 1;
            call[1] = found ? transaction : null;
            return found;
        });
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, new LeanProofStore(), verifier);
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction.Hash)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        ProofWrapperAcceptance first = await service.AcceptDetailedAsync(wrapper);
        Assert.That(first.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.PoolRejected));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        missingAfterPreflight = false;
        ProofWrapperAcceptance retry = await service.AcceptDetailedAsync(wrapper);
        Assert.That(retry.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Local_pool_exceptions_are_nonfatal_and_do_not_retain_unadmitted_proofs([Values] bool argumentError)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        ITxPool pool = Substitute.For<ITxPool>();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(_ =>
            throw (argumentError ? new ArgumentException("local argument") : new InvalidOperationException("local state")));
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        FakeLeanProofVerifier verifier = new(true);
        LeanProofStore store = new();
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction)],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        ProofWrapperAcceptance result = await service.AcceptDetailedAsync(wrapper);
        Assert.That(result.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.LocalFailure));
        Assert.That(store.Covers(transaction), Is.False);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
    }

    [Test]
    public void Public_snapshot_reads_never_invoke_the_prover_and_return_owned_buffers()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(1, 0);
        Assert.That(service.GetLatestWrapper().IsSuccess, Is.False);
        byte[] built = service.BuildWrapper().Data!;
        ValueHash256 expected = ValueKeccak.Compute(built);
        for (int i = 0; i < 10; i++)
        {
            byte[] snapshot = service.GetLatestWrapper().Data!;
            Assert.That(ValueKeccak.Compute(snapshot), Is.EqualTo(expected));
            snapshot[0] ^= 0xff;
        }
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
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
    public void Retained_large_mixed_roots_remain_eligible_for_gossip([Values] bool sameParent)
    {
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize([
            new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("large-root-signature"), default),
            new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute("large-root-stark"), default)]);
        byte[] proof = new byte[5 * 1024 * 1024];
        LeanProofStore store = new();
        if (sameParent) store.AddVerified(dependencies, null, proof);
        else
        {
            for (int i = 0; i < dependencies.Count; i++)
            {
                proof[^1] = (byte)i;
                store.AddVerified([dependencies[i]], null, proof);
            }
        }
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null,
                Eip8288Constants.LeanSphincsVerificationGas + Eip8288Constants.LeanStarkVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize(dependencies))]
        };
        transaction.Hash = transaction.CalculateHash();
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService([transaction], store, verifier);
        Assert.That(store.TryGetInput(dependencies, out AggregationInput input), Is.True);
        Assert.That(RecursiveStarkAggregator.InputSize(input), Is.GreaterThan(RecursiveStarkAggregator.MaxProductionWitnessBytes));
        Assert.That(RecursiveStarkAggregator.InputSize(input), Is.LessThan(Eip8288Constants.MaxAggregationInputBytes));

        Result<byte[]> result = service.BuildWrapper();

        Assert.That(result.IsSuccess, Is.True, result.Error);
        MempoolWrapper wrapper = Decode(result.Data!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Transactions, Has.Count.EqualTo(1));
            Assert.That(wrapper.Transactions[0].Full!.Hash, Is.EqualTo(transaction.Hash));
            Assert.That(wrapper.Deps, Is.EqualTo(dependencies));
            Assert.That(verifier.ProofCalls, Is.EqualTo(sameParent ? 0 : 1));
            Assert.That(result.Data!.Length, Is.LessThanOrEqualTo(LeanProofStore.MaxWrapperBytes));
            Assert.That(store.TryGetInput(dependencies, out _), Is.True);
        }
        if (sameParent) Assert.That(wrapper.RecursiveStark!.StarkProof, Is.EqualTo(proof));
    }

    [Test]
    public void Large_discarded_parent_coverage_is_skipped_before_proving()
    {
        LeanProofStore store = new();
        Transaction[] transactions = new Transaction[2];
        for (int i = 0; i < transactions.Length; i++)
        {
            FrameDependency[] parent = Enumerable.Range(0, Eip8288Constants.MaxProofDependencies / 2 + 1).Select(index => new FrameDependency(
                Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"{i}:{index}"), default)).ToArray();
            store.AddVerified(parent, null, [(byte)i]);
            transactions[i] = new Transaction
            {
                Type = TxType.FrameTx,
                NonceKeys = [UInt256.Zero],
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
            NonceKeys = [UInt256.Zero],
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
            NonceKeys = [UInt256.Zero],
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
    public async Task Pool_rejected_direct_witnesses_do_not_consume_pending_proof_capacity()
    {
        LeanProofStore store = new();
        ITxPool pool = Substitute.For<ITxPool>();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            Transaction transaction = call.Arg<Transaction>();
            if (transaction.Nonce == 1) return AcceptTxResult.FeeTooLow;
            Assert.That(store.PinPending(transaction), Is.True);
            return AcceptTxResult.Accepted;
        });
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, new FakeLeanProofVerifier(true));
        byte[] rejectedWitness = new byte[4 * 1024 * 1024];
        for (int i = 0; i < 20; i++)
        {
            FrameDependency accepted = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"accepted:{i}"), default);
            FrameDependency rejected = new(Eip8288Constants.LeanStarkScheme, ValueKeccak.Compute($"rejected:{i}"), default);
            static Transaction Transaction(FrameDependency dependency, ulong nonce) => new()
            {
                Type = TxType.FrameTx,
                NonceKeys = [UInt256.Zero],
                ChainId = 1,
                SenderAddress = Address.Zero,
                Nonce = nonce,
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null,
                    dependency.Scheme == Eip8288Constants.LeanSphincsScheme
                        ? Eip8288Constants.LeanSphincsVerificationGas : Eip8288Constants.LeanStarkVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
            };
            Transaction first = Transaction(accepted, 0), later = Transaction(rejected, 1);
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = [new WrapperTransaction(first), new WrapperTransaction(later)],
                Deps = [accepted, rejected],
                Mode = MempoolWrapper.ModeDirect,
                Proofs = [new byte[Eip8288Constants.LeanSphincsWitnessBytes], rejectedWitness]
            }).Bytes;
            Assert.That((await service.AcceptDetailedAsync(encoded)).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.PoolRejected));
            Assert.That(store.Covers(first), Is.True);
            Assert.That(store.Covers(later), Is.False);
        }
    }

    [Test]
    public async Task Native_admission_is_offloaded_bounded_and_owns_its_input_snapshot()
    {
        (ProofWrapperService producer, _) = Create(1, 0);
        byte[] encoded = producer.BuildWrapper().Data!;
        byte[] original = (byte[])encoded.Clone();
        FakeLeanProofVerifier verifier = new(true);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        verifier.OnVerification = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        ProofWrapperService service = CreateService([], new LeanProofStore(), verifier);
        Task<ProofWrapperAcceptance> first = service.AcceptDetailedAsync(encoded);
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(first.IsCompleted, Is.False);
            encoded[0] ^= 0xff;
            Assert.That((await service.AcceptDetailedAsync(original)).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Busy));
        }
        finally { release.Set(); }
        Assert.That((await first).HasValidProof, Is.True);
        verifier.OnVerification = null;
        Assert.That((await service.AcceptDetailedAsync(original)).HasValidProof, Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1), "the memo belongs to the original owned bytes");
    }

    [Test]
    public void Aggregation_reuses_proofs_and_cooperatively_cancels()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(1, 0);
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
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
            NonceKeys = [UInt256.Zero],
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
            NonceKeys = [UInt256.Zero],
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
            byte[] encoded = InclusionListProofPackageDecoder.Instance.Encode(new InclusionListProofPackage
            {
                Transactions = transactions,
                ProvenDependencies = Eip8288Dependencies.Serialize(dependencies),
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
            NonceKeys = [UInt256.Zero],
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

    private static ProofWrapperService CreateService(Transaction[] transactions, LeanProofStore store, FakeLeanProofVerifier verifier, IReleaseSpec? spec = null)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(transactions);
        pool.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        return new(pool, new TestSingleReleaseSpecProvider(spec ?? Eip8288Prototype.Instance), finder, store, verifier);
    }

    private static MempoolWrapper Decode(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        return MempoolWrapperDecoder.Instance.Decode(ref reader)!;
    }
}
