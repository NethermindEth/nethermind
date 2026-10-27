// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
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
    [Test]
    public async Task Zero_dependency_hashes_bind_the_containing_transaction_in_both_wrapper_modes([Values(MempoolWrapper.ModeDirect, MempoolWrapper.ModeRecursive)] byte mode)
    {
        FrameDependency raw = new(Eip8288Constants.LeanSphincsScheme, default, TestItem.KeccakA.ValueHash256);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            NonceKeys = [UInt256.Zero],
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([raw]))]
        };
        transaction.Hash = transaction.CalculateHash();
        FrameDependency resolved = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue)[0];
        bool[] claims = [true, false];
        foreach (bool literalZero in claims)
        {
            FrameDependency claim = literalZero ? raw : resolved;
            LeanProofStore store = new(FrameTxSigHash.ComputeValue);
            ProofWrapperService service = CreateService([], store, new FakeLeanProofVerifier(true));
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = [transaction],
                Deps = [claim],
                Mode = mode,
                Proofs = mode == MempoolWrapper.ModeDirect ? [[1]] : null,
                RecursiveStark = mode == MempoolWrapper.ModeRecursive ? new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([claim]))) : null
            }).Bytes;

            ProofWrapperAcceptance result = await service.AcceptDetailedAsync(encoded);

            Assert.That(result.Result.IsSuccess, Is.EqualTo(!literalZero), result.Result.Error);
            Assert.That(store.Covers(transaction), Is.EqualTo(!literalZero));
            if (literalZero) Assert.That(result.Result.Error, Does.Contain(MempoolWrapperValidator.DepsMismatch));
        }
        Assert.That(Eip8288Dependencies.ParseFrame(transaction.Frames[0]).Single(), Is.EqualTo(raw));
    }

    [Test]
    public async Task Transport_owned_body_avoids_an_encoded_copy([Values] bool owned)
    {
        (ProofWrapperService producer, _) = Create(1, 0);
        byte[] encoded = producer.BuildWrapper().Data!;
        (ProofWrapperService receiver, FakeLeanProofVerifier verifier) = Create(0, 0);
        byte[]? published = null;
        receiver.WrapperValidated += (body, _) => published = body;

        ProofWrapperAcceptance accepted = owned
            ? await receiver.AcceptOwnedDetailedAsync(encoded)
            : await receiver.AcceptDetailedAsync(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.HasValidProof, Is.True, accepted.Result.Error);
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
            Assert.That(ReferenceEquals(published, encoded), Is.EqualTo(owned));
            Assert.That(published, Is.EqualTo(encoded));
        }
    }

    [TestCase("assert")]
    [TestCase("keyed")]
    [TestCase("scalar")]
    public async Task Frame_fork_mismatch_is_local_and_can_be_retried_without_a_negative_memo(string scenario)
    {
        OverridableReleaseSpec spec = new(Eip8288Prototype.Instance)
        {
            IsEip7906Enabled = scenario != "assert",
            IsEip8250Enabled = scenario != "keyed"
        };
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        TxFrame dependencyFrame = new(FrameMode.DepVerify, FrameFlags.None, null,
            Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize([dependency]));
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            NonceKeys = scenario == "scalar" ? null : [UInt256.Zero],
            Frames = scenario == "assert"
                ? [dependencyFrame, new(FrameMode.Assert, FrameFlags.None, Address.Zero, 1000, UInt256.Zero, default)]
                : [dependencyFrame]
        };
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService([], new LeanProofStore(FrameTxSigHash.ComputeValue), verifier, spec);
        byte[] Encode() => MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
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
        Assert.That((await service.AcceptDetailedAsync(wrapper)).HasValidProof, Is.True);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));

        transaction.Frames![0] = new(FrameMode.DepVerify, (FrameFlags)0xff, null,
            Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize([dependency]));
        Assert.That((await service.AcceptDetailedAsync(Encode())).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Invalid));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1), "intrinsic malformed data is rejected before verification");
    }

    [Test]
    public async Task Oversized_dependency_frames_are_rejected_before_proof_verification()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(0, 0);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
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
        Result<Hash256[]> accepted = await service.AcceptAsync(MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
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
    public async Task Scalar_nonce_is_rejected_before_proof_verification()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(0, 0);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1,
            SenderAddress = Address.Zero,
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        RecursiveStark recursive = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency])));
        Result<Hash256[]> accepted = await service.AcceptAsync(MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
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
    public async Task Pool_rejection_retains_the_verified_verdict_for_a_retry()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        Transaction transaction = CreateTransaction(dependency, TestItem.KeccakA);
        ITxPool pool = Substitute.For<ITxPool>();
        int submissions = 0;
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>())
            .Returns(_ => ++submissions == 1 ? AcceptTxResult.FeeTooLow : AcceptTxResult.Accepted);
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, new LeanProofStore(FrameTxSigHash.ComputeValue), verifier);
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        ProofWrapperAcceptance first = await service.AcceptDetailedAsync(wrapper);
        Assert.That(first.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.PoolRejected));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        ProofWrapperAcceptance retry = await service.AcceptDetailedAsync(wrapper);
        Assert.That(retry.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
    }

    // EIP-8437: proof and dependency-rule failures are discarded without penalty, so they are reported apart from malformed
    // encodings, which remain penalizable.
    [TestCase("valid", ProofWrapperAcceptanceStatus.Accepted)]
    [TestCase("proof", ProofWrapperAcceptanceStatus.Invalid)]
    [TestCase("union", ProofWrapperAcceptanceStatus.Invalid)]
    [TestCase("limit", ProofWrapperAcceptanceStatus.Invalid)]
    [TestCase("reference", ProofWrapperAcceptanceStatus.Malformed)]
    [TestCase("order", ProofWrapperAcceptanceStatus.Malformed)]
    public async Task Proof_and_dependency_failures_are_invalid_and_encoding_failures_malformed(string scenario, ProofWrapperAcceptanceStatus expected)
    {
        // Seventeen signature dependencies across two transactions exceed MAX_LEANSIG_DEPS_PER_WRAPPER.
        Transaction[] transactions = [.. Enumerable.Range(0, 2).Select(i =>
        {
            int perTransaction = scenario == "limit" ? 9 - i : 1;
            Transaction transaction = CreateTransaction(new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"sig{i}"), default), Keccak.Compute($"tx{i}"));
            FrameDependency[] own = [.. Enumerable.Range(0, perTransaction)
                .Select(j => new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"sig{i}:{j}"), default))];
            transaction.Frames![0] = new(FrameMode.DepVerify, FrameFlags.None, null, (ulong)perTransaction * Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize(own));
            transaction.Hash = transaction.CalculateHash();
            return transaction;
        }).OrderBy(static tx => tx.Hash!.ToString())];
        if (scenario != "limit") transactions = [transactions[0]];
        List<FrameDependency> deps = Eip8288Dependencies.Canonicalize(
            transactions.SelectMany(static tx => Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue)));
        if (scenario == "union") deps = [new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("other"), default)];
        byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = transactions,
            Deps = deps,
            Mode = MempoolWrapper.ModeRecursive,
            RecursiveStark = new RecursiveStark([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)))
        }).Bytes;
        byte[] transaction = MempoolWrapperDecoder.EncodeTransaction(transactions[0]);
        Rlp proofContent = Rlp.Encode(Rlp.Encode([Rlp.Encode(Eip8288Dependencies.Serialize(deps))]), Rlp.Encode(new byte[] { 1 }));
        if (scenario == "reference")
            encoded = Rlp.Encode(Rlp.Encode([Rlp.Encode(Rlp.Encode(Keccak.Compute(transaction)))]), Rlp.Encode(1UL), proofContent).Bytes;
        if (scenario == "order")
            encoded = Rlp.Encode(Rlp.Encode([Rlp.Encode(transaction), Rlp.Encode(transaction)]), Rlp.Encode(1UL), proofContent).Bytes;
        ITxPool pool = Substitute.For<ITxPool>();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(AcceptTxResult.Accepted);
        ProofWrapperService service = CreateService([], new LeanProofStore(FrameTxSigHash.ComputeValue), new FakeLeanProofVerifier(scenario != "proof"), pool: pool);

        ProofWrapperAcceptance result = await service.AcceptDetailedAsync(encoded);

        Assert.That(result.Status, Is.EqualTo(expected), result.Result.Error);
    }

    [Test]
    public async Task Local_pool_exceptions_are_nonfatal_and_do_not_retain_unadmitted_proofs([Values] bool argumentError)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
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
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, verifier);
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
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
    public void Unchanged_background_refresh_does_not_allocate_an_encoded_wrapper_copy()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(1, 0);
        verifier.ProofOutput = new byte[1024 * 1024];
        Assert.That(service.RefreshWrapper(), Is.EqualTo(Result.Success));
        byte[] initial = service.GetLatestWrapper().Data!;
        Assert.That(initial.Length, Is.GreaterThan(1024 * 1024));
        Assert.That(service.RefreshWrapper(), Is.EqualTo(Result.Success));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Result refreshed = service.RefreshWrapper();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshed, Is.EqualTo(Result.Success));
            Assert.That(allocated, Is.LessThan(initial.Length / 2), "unchanged refresh does not copy the large encoded bytes");
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
            Assert.That(service.GetLatestWrapper().Data, Is.EqualTo(initial));
        }
    }

    [Test]
    public void Known_hash_refresh_skips_bytes_but_rechecks_membership_and_changed_snapshots_are_owned()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        Transaction first = CreateTransaction(dependency, TestItem.KeccakA);
        Transaction second = CreateTransaction(dependency, TestItem.KeccakB);
        Transaction[] pending = [first];
        ITxPool pool = Substitute.For<ITxPool>();
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([dependency], [[1]], null);
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService(pending, store, verifier, pool: pool);
        pool.GetPendingTransactions().Returns(_ => pending);
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        Result<byte[]> initial = service.GetLatestWrapper(null, out ValueHash256 firstHash);
        Assert.That(firstHash, Is.EqualTo(ValueKeccak.Compute(initial.Data!)));
        initial.Data![0] ^= 0xff;
        Assert.That(ValueKeccak.Compute(service.GetLatestWrapper().Data!), Is.EqualTo(firstHash));
        Assert.That(service.GetLatestWrapper(firstHash, out ValueHash256 unchanged).Data, Is.Empty);
        Assert.That(unchanged, Is.EqualTo(firstHash));
        pending = [first, second];
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        Result<byte[]> changed = service.GetLatestWrapper(firstHash, out ValueHash256 secondHash);
        Assert.That(changed.Data, Is.Not.Empty);
        Assert.That(secondHash, Is.EqualTo(ValueKeccak.Compute(changed.Data!)));
        Assert.That(secondHash, Is.Not.EqualTo(firstHash));
        pending = [];
        Assert.That(service.GetLatestWrapper(secondHash, out _).IsSuccess, Is.False);
        Assert.That(verifier.ProofCalls, Is.EqualTo(1), "transaction changes sharing the same dependency reuse the proof");
    }

    [TestCase(false, true)]
    [TestCase(true, true)]
    [TestCase(false, false)]
    [TestCase(true, false)]
    public void Completed_proving_is_authenticated_once_for_cached_and_custom_backends(bool decorated, bool valid)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([dependency], [[1]], null);
        FakeLeanProofVerifier backend = new(valid);
        ILeanProofVerifier verifier = decorated ? new ProductionProofCache(backend) : backend;
        ProofWrapperService service = CreateService([CreateTransaction(dependency, TestItem.KeccakA)], store, verifier);
        Assert.That(service.BuildWrapper().IsSuccess, Is.EqualTo(valid));
        Assert.That(backend.VerificationCalls, Is.EqualTo(1));
        Assert.That(backend.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public void Exact_parent_shortcut_remains_authenticated_with_the_completed_work_cache()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        FakeLeanProofVerifier backend = new(false);
        ProductionProofCache verifier = new(backend);
        AggregationInput input = new() { RecursiveProofs = [new([dependency], [1])] };
        Assert.Throws<InvalidOperationException>(() => RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, hash));
        Assert.That(backend.VerificationCalls, Is.EqualTo(1));
        Assert.That(backend.ProofCalls, Is.Zero);
    }

    [Test]
    public async Task Pool_removal_during_proving_prevents_publication_but_retains_verified_work()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        Transaction transaction = CreateTransaction(dependency, TestItem.KeccakA);
        ITxPool pool = Substitute.For<ITxPool>();
        Transaction[] pending = [transaction];
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([dependency], [[1]], null);
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService(pending, store, verifier, pool: pool);
        pool.GetPendingTransactions().Returns(_ => Volatile.Read(ref pending));
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        verifier.OnProving = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Build release timed out");
        };
        Task<Result<byte[]>> build = Task.Run(() => service.BuildWrapper());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref pending, []);
        }
        finally { release.Set(); }
        Result<byte[]> result = await build.WaitAsync(TimeSpan.FromSeconds(5));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Error, Does.Contain("selection changed"));
            Assert.That(service.GetLatestWrapper().IsSuccess, Is.False);
            Assert.That(store.TryGetRecursiveProof([dependency], out _), Is.True, "authenticated completed work survives pool churn");
        }
    }

    [Test]
    public void Previous_own_aggregate_is_reused_for_additions_and_authenticated_when_pruned([Values] bool rejectDiscardedParent)
    {
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("first"), default);
        FrameDependency second = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("second"), default);
        Transaction a = CreateTransaction(first, TestItem.KeccakA);
        Transaction b = CreateTransaction(second, TestItem.KeccakB);
        Transaction[] pending = [a];
        ITxPool pool = Substitute.For<ITxPool>();
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([first, second], [[1], [1]], null);
        RecordingVerifier verifier = new();
        ProofWrapperService service = CreateService(pending, store, verifier, pool: pool);
        pool.GetPendingTransactions().Returns(_ => pending);
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        pending = [a, b];
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        FrameDependency[] expectedAddition = [second];
        FrameDependency[] expectedParent = [first];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.Inputs[1].Deps, Is.EqualTo(expectedAddition));
            Assert.That(verifier.Inputs[1].RecursiveProofs, Has.Count.EqualTo(1));
            Assert.That(verifier.Inputs[1].RecursiveProofs[0].InnerDeps, Is.EqualTo(expectedParent));
        }
        // Remove the dependency whose singleton proof was never retained.
        pending = [b];
        ValueHash256 parentHash = Eip8288Dependencies.ComputeDepsHash([first, second]);
        verifier.RejectedStatement = rejectDiscardedParent ? parentHash : null;
        int before = verifier.VerifiedStatements.Count;
        Result<byte[]> pruned = service.BuildWrapper();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pruned.IsSuccess, Is.EqualTo(!rejectDiscardedParent), pruned.Error);
            Assert.That(verifier.Inputs[2].Deps, Is.Empty);
            Assert.That(verifier.Inputs[2].RecursiveProofs, Has.Count.EqualTo(1));
            Assert.That(verifier.Inputs[2].Discards, Is.EqualTo(expectedParent));
            Assert.That(verifier.VerifiedStatements.Skip(before), Does.Contain(parentHash), "discarded origins still require authentication");
        }
    }

    private sealed class RecordingVerifier : ILeanProofVerifier
    {
        public List<AggregationInput> Inputs { get; } = [];
        public List<ValueHash256> VerifiedStatements { get; } = [];
        public ValueHash256? RejectedStatement { get; set; }
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
        {
            VerifiedStatements.Add(depsHash);
            return RejectedStatement != depsHash;
        }
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            Inputs.Add(input);
            if (!RecursiveStarkAggregatorTests.TryAggregate(input, this, Eip8288Verifier.Pinned, out _, out ValueHash256 actual) || actual != depsHash)
                throw new InvalidOperationException("Invalid proving input.");
            return [1];
        }
    }

    /// <summary>A frame transaction declaring <paramref name="dependency"/>, distinguished by a nonce derived from <paramref name="key"/>.</summary>
    private static Transaction CreateTransaction(FrameDependency dependency, Hash256 key)
    {
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = 1,
            SenderAddress = Address.Zero,
            Nonce = BitConverter.ToUInt32(key.Bytes[..4]),
            Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
        };
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    // EIP-8288: produced wrappers carry their full transactions, within MAX_WRAPPER_BYTES less the room an aggregate proof needs.
    [Test]
    public void Produced_wrappers_carry_full_transactions_within_the_byte_budget()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(10, 128 * 1024, out Transaction[] transactions);
        byte[] first = service.BuildWrapper().Data!;
        MempoolWrapper decoded = Decode(first);
        byte[] second = service.BuildWrapper().Data!;
        MempoolWrapper rotated = Decode(second);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Transactions, Has.Count.LessThan(10));
            Assert.That(first.Length, Is.LessThanOrEqualTo(Eip8288Constants.MaxWrapperBytes - Eip8288Constants.MaxAggregateProofBytes));
            Assert.That(decoded.Transactions.Select(static tx => tx.Hash), Is.SubsetOf(transactions.Select(static tx => tx.Hash)));
            Assert.That(decoded.Transactions.Select(MempoolWrapperDecoder.EncodeTransaction),
                Is.EqualTo(decoded.Transactions.Select(tx => MempoolWrapperDecoder.EncodeTransaction(transactions.Single(t => t.Hash == tx.Hash)))));
            Assert.That(rotated.Transactions.Select(static tx => tx.Hash), Is.Not.EqualTo(decoded.Transactions.Select(static tx => tx.Hash)),
                "the next build rotates to the remaining transactions");
            Assert.That(verifier.ProofCalls, Is.EqualTo(1), "transactions sharing a dependency reuse the proof");
        }
    }

    // EIP-8288: every AGGREGATION_INTERVAL a node produces mode-1 wrappers over bounded subsets, so one cycle covers every pending
    // dependency-bearing transaction, however many wrappers that takes.
    [Test]
    public void One_cycle_produces_bounded_wrappers_covering_every_pending_transaction()
    {
        (ProofWrapperService service, FakeLeanProofVerifier verifier) = Create(10, 128 * 1024, out Transaction[] transactions);
        List<MempoolWrapper> produced = [];
        Result result = service.RefreshWrapper(() => produced.Add(Decode(service.GetLatestWrapper().Data!)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(Result.Success));
            Assert.That(produced, Has.Count.GreaterThan(1));
            Assert.That(produced.SelectMany(static w => w.Transactions).Select(static tx => tx.Hash),
                Is.EquivalentTo(transactions.Select(static tx => tx.Hash)), "each transaction is in exactly one wrapper of the cycle");
            Assert.That(verifier.ProofCalls, Is.EqualTo(1), "subsets sharing a dependency reuse the proof");
        }
    }

    // A cycle stops proving once its budget is spent, and the next resumes the rotation where it stopped.
    [Test]
    public void Exhausted_cycle_budget_defers_the_remaining_subsets_to_the_next_cycle()
    {
        (ProofWrapperService service, _) = Create(10, 128 * 1024, out Transaction[] transactions);
        List<MempoolWrapper> first = [], second = [];
        Assert.That(service.RefreshWrapper(() => first.Add(Decode(service.GetLatestWrapper().Data!)), TimeSpan.Zero), Is.EqualTo(Result.Success));
        Assert.That(service.RefreshWrapper(() => second.Add(Decode(service.GetLatestWrapper().Data!)), TimeSpan.Zero), Is.EqualTo(Result.Success));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Count.EqualTo(1));
            Assert.That(second, Has.Count.EqualTo(1));
            Assert.That(second[0].Transactions.Select(static tx => tx.Hash), Is.Not.SubsetOf(first[0].Transactions.Select(static tx => tx.Hash)),
                "the next cycle continues the rotation");
            Assert.That(first[0].Transactions.Count + second[0].Transactions.Count, Is.LessThanOrEqualTo(transactions.Length));
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
    public void Retained_large_roots_remain_eligible_for_gossip([Values] bool sameParent)
    {
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize([
            new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("large-root-signature"), default),
            new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("large-root-second"), default)]);
        byte[] proof = new byte[Eip8288Constants.MaxAggregateProofBytes];
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
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
                2 * Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize(dependencies))]
        };
        transaction.Hash = transaction.CalculateHash();
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService([transaction], store, verifier);
        Assert.That(store.TryGetInput(dependencies, out AggregationInput input), Is.True);
        Assert.That(RecursiveStarkAggregator.InputSize(input), Is.GreaterThan(proof.Length));
        Assert.That(RecursiveStarkAggregator.InputSize(input), Is.LessThan(Eip8288Constants.MaxAggregationInputBytes));

        Result<byte[]> result = service.BuildWrapper();

        Assert.That(result.IsSuccess, Is.True, result.Error);
        MempoolWrapper wrapper = Decode(result.Data!);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.Transactions, Has.Count.EqualTo(1));
            Assert.That(wrapper.Transactions[0].Hash, Is.EqualTo(transaction.Hash));
            Assert.That(wrapper.Deps, Is.EqualTo(dependencies));
            Assert.That(verifier.ProofCalls, Is.EqualTo(sameParent ? 0 : 1));
            Assert.That(result.Data!.Length, Is.LessThanOrEqualTo(Eip8288Constants.MaxWrapperBytes));
            Assert.That(store.TryGetInput(dependencies, out _), Is.True);
        }
        if (sameParent) Assert.That(wrapper.RecursiveStark!.StarkProof, Is.EqualTo(proof));
    }

    [Test]
    public void Large_discarded_parent_coverage_is_skipped_before_proving()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
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
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
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
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
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
            Transactions = [.. new[] { transaction, later }.OrderBy(static tx => tx.CalculateHash().ToString()).Select(static tx => tx)],
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
            Assert.That((await service.AcceptDetailedAsync([0])).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Malformed));
            Assert.That((await service.AcceptAsync(encoded)).IsSuccess, Is.False);
        }
    }

    [Test]
    public async Task Pool_rejected_direct_witnesses_do_not_consume_pending_proof_capacity()
    {
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
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
        byte[] rejectedWitness = new byte[Eip8288Constants.LeanSphincsWitnessBytes];
        for (int i = 0; i < 40; i++)
        {
            FrameDependency accepted = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"accepted:{i}"), default);
            FrameDependency rejected = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"rejected:{i}"), default);
            static Transaction Transaction(FrameDependency dependency, ulong nonce) => new()
            {
                Type = TxType.FrameTx,
                NonceKeys = [UInt256.Zero],
                ChainId = 1,
                SenderAddress = Address.Zero,
                Nonce = nonce,
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
            };
            Transaction first = Transaction(accepted, 0), later = Transaction(rejected, 1);
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
            {
                Transactions = [.. new[] { first, later }.OrderBy(static tx => tx.CalculateHash().ToString())],
                Deps = Eip8288Dependencies.Canonicalize([accepted, rejected]),
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
        ProofWrapperService service = CreateService([], new LeanProofStore(FrameTxSigHash.ComputeValue), verifier);
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
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
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
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
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
            Transactions = [transaction],
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
    public void Cancellation_preserves_only_successfully_admitted_coverage([Values] bool firstAccepted)
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
        }).OrderBy(static transaction => transaction.CalculateHash().ToString()).ToArray();
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize([first, second]);
        ITxPool pool = Substitute.For<ITxPool>();
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(call =>
        {
            Assert.That(store.Covers(call.Arg<Transaction>()), Is.True);
            cancellation.Cancel();
            return firstAccepted ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow;
        });
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.Head.Returns(Build.A.Block.TestObject);
        ProofWrapperService service = new(pool, new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), finder, store, new FakeLeanProofVerifier(true));
        byte[] encoded = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = transactions,
            Deps = dependencies,
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1], [1]]
        }).Bytes;
        Assert.That(async () => await service.AcceptAsync(encoded, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Covers(transactions[0]), Is.EqualTo(firstAccepted));
            Assert.That(store.Covers(transactions[1]), Is.False);
            pool.Received(1).SubmitTx(Arg.Is<Transaction>(transaction => transaction.Nonce == transactions[0].Nonce), TxHandlingOptions.PersistentBroadcast);
            pool.DidNotReceive().SubmitTx(Arg.Is<Transaction>(transaction => transaction.Nonce == transactions[1].Nonce), Arg.Any<TxHandlingOptions>());
        }
    }

    [Test]
    public async Task Full_transactions_are_admitted_and_the_wrapper_is_reported_valid()
    {
        Transaction transaction = CreateHashedTransaction(TestItem.KeccakA);
        (ProofWrapperService service, ITxPool pool, byte[] wrapper) = CreateWrapper(AcceptTxResult.Accepted, transaction);
        List<byte[]> validated = [];
        service.WrapperValidated += (encoded, _) => validated.Add(encoded);

        ProofWrapperAcceptance accepted = await service.AcceptDetailedAsync(wrapper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted), accepted.Result.Error);
            Assert.That(accepted.Result.Data, Is.EqualTo(new[] { transaction.Hash }));
            Assert.That(validated, Has.Count.EqualTo(1));
            Assert.That(validated[0], Is.EqualTo(wrapper));
            pool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => tx.Hash == transaction.Hash), TxHandlingOptions.PersistentBroadcast);
        }
    }

    // A wrapper is relayed only once usable for admission, so a verified proof over transactions the pool
    // rejects (for example unfunded) is not republished.
    [TestCase(true)]
    [TestCase(false)]
    public async Task Wrapper_is_relayed_only_when_admission_succeeds(bool poolAccepts)
    {
        Transaction transaction = CreateHashedTransaction(TestItem.KeccakA);
        (ProofWrapperService service, _, byte[] wrapper) =
            CreateWrapper(poolAccepts ? AcceptTxResult.Accepted : AcceptTxResult.InsufficientFunds, transaction);
        int relayed = 0;
        service.WrapperValidated += (_, _) => relayed++;

        ProofWrapperAcceptance result = await service.AcceptDetailedAsync(wrapper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(poolAccepts ? ProofWrapperAcceptanceStatus.Accepted : ProofWrapperAcceptanceStatus.PoolRejected));
            Assert.That(relayed, Is.EqualTo(poolAccepts ? 1 : 0));
        }
    }

    // An already-pending funded transaction must not carry rejected junk through relay: admission has to succeed for every entry.
    [Test]
    public async Task Wrapper_with_a_pending_transaction_and_rejected_junk_is_not_relayed()
    {
        Transaction pending = CreateHashedTransaction(TestItem.KeccakA);
        Transaction junk = CreateHashedTransaction(TestItem.KeccakB);
        (ProofWrapperService service, ITxPool pool, byte[] wrapper) = CreateWrapper(AcceptTxResult.InsufficientFunds, pending, junk);
        pool.TryGetPendingTransaction(pending.Hash!.ValueHash256, out Arg.Any<Transaction?>()).Returns(call =>
        {
            call[1] = pending;
            return true;
        });
        int relayed = 0;
        service.WrapperValidated += (_, _) => relayed++;

        ProofWrapperAcceptance result = await service.AcceptDetailedAsync(wrapper);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Status, Is.EqualTo(ProofWrapperAcceptanceStatus.PoolRejected));
            Assert.That(relayed, Is.Zero);
        }
    }

    private static Transaction CreateHashedTransaction(Hash256 key)
    {
        Transaction transaction = CreateTransaction(new(Eip8288Constants.LeanSphincsScheme, key.ValueHash256, default), key);
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    private static (ProofWrapperService Service, ITxPool Pool, byte[] Wrapper) CreateWrapper(AcceptTxResult submitResult, params Transaction[] transactions)
    {
        ITxPool pool = Substitute.For<ITxPool>();
        pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(submitResult);
        ProofWrapperService service = CreateService([], new LeanProofStore(FrameTxSigHash.ComputeValue), new FakeLeanProofVerifier(true), pool: pool);
        List<FrameDependency> dependencies = Eip8288Dependencies.Canonicalize(
            transactions.SelectMany(static transaction => Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue)));
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [.. transactions.OrderBy(static transaction => transaction.Hash!.ToString())],
            Deps = dependencies,
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [.. dependencies.Select(static _ => new byte[] { 1 })]
        }).Bytes;
        return (service, pool, wrapper);
    }

    [TestCase(Eip8288Constants.MaxWrapperBytes + 1, ProofWrapperAcceptanceStatus.Malformed)]
    public async Task Wrapper_above_max_wrapper_bytes_is_malformed(int size, ProofWrapperAcceptanceStatus expected)
    {
        FakeLeanProofVerifier verifier = new(true);
        ProofWrapperService service = CreateService([], new LeanProofStore(FrameTxSigHash.ComputeValue), verifier);
        Assert.That((await service.AcceptDetailedAsync(new byte[size])).Status, Is.EqualTo(expected));
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    [Test]
    public async Task Verifier_fault_during_the_upgrade_rule_is_a_local_failure_and_retried_on_the_next_refresh()
    {
        FrameDependency direct = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("direct"), default);
        FrameDependency aggregated = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("aggregated"), default);
        Transaction aggregatedTx = CreateTransaction(aggregated, TestItem.KeccakB);
        aggregatedTx.Hash = aggregatedTx.CalculateHash();
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([direct], [[1]], null);
        store.AddVerified([aggregated], null, [2]);
        FakeLeanProofVerifier verifier = new(true) { OnVerification = static () => throw new InvalidOperationException("native fault") };
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([aggregatedTx]);
        ValueHash256 upgradedVk = ValueKeccak.Compute("next AGGREGATED_VK");
        CustomSpecProvider specs = new(((ForkActivation)0, Eip8288Prototype.Instance),
            (new ForkActivation(0, 1_000), new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = upgradedVk }));
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        Block head = HeadWithParent(finder, 9, 980);
        finder.Head.Returns(_ => head);
        ProofWrapperService service = new(pool, specs, finder, store, verifier);
        List<Eip8288Verifier> changes = [];
        service.VerifierChanged += changes.Add;
        service.RefreshVerifier();
        head = HeadWithParent(finder, 10, 990);

        Assert.That(() => service.RefreshVerifier(), Throws.InvalidOperationException);
        Assert.That(service.CurrentVerifier, Is.EqualTo(Eip8288Verifier.Pinned), "a half-applied upgrade is not committed");
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await service.AcceptDetailedAsync([0xc0])).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.LocalFailure));
            Assert.That(service.GetLatestWrapper().IsSuccess, Is.False);
            Assert.That(service.BuildWrapper().IsSuccess, Is.False);
        }
        verifier.OnVerification = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(service.RefreshVerifier(), Is.EqualTo(new Eip8288Verifier(upgradedVk)));
            Assert.That(changes, Is.EqualTo(new[] { new Eip8288Verifier(upgradedVk) }));
            Assert.That(store.Covers(aggregatedTx), Is.False, "the retried upgrade drops every aggregate proof");
            pool.Received(1).RemoveTransaction(aggregatedTx.Hash);
        }
    }

    // Local verifier-change rule: once the local head reaches a target fork with another AGGREGATED_VK, every held aggregate proof
    // is dropped, a direct witness whose scheme parameters are unchanged is kept by reusing its verdict, and pending
    // transactions left without proof coverage leave the pool.
    [Test]
    public void Verifier_change_applies_the_upgrade_rule()
    {
        FrameDependency direct = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("direct"), default);
        FrameDependency aggregated = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("aggregated"), default);
        Transaction directTx = CreateTransaction(direct, TestItem.KeccakA);
        Transaction aggregatedTx = CreateTransaction(aggregated, TestItem.KeccakB);
        directTx.Hash = directTx.CalculateHash();
        aggregatedTx.Hash = aggregatedTx.CalculateHash();
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([direct], [[1]], null);
        store.AddVerified([aggregated], null, [2]);
        FakeLeanProofVerifier verifier = new(true);
        ProofVerdictCache.VerifyDirect(verifier, Eip8288Verifier.Pinned, direct, [1]);
        ITxPool pool = Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns([directTx, aggregatedTx]);
        ValueHash256 upgradedVk = ValueKeccak.Compute("next AGGREGATED_VK");
        CustomSpecProvider specs = new(((ForkActivation)0, Eip8288Prototype.Instance),
            (new ForkActivation(0, 1_000), new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = upgradedVk }));
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        Block head = HeadWithParent(finder, 9, 980);
        finder.Head.Returns(_ => head);
        ProofWrapperService service = new(pool, specs, finder, store, verifier);
        List<Eip8288Verifier> changes = [];
        service.VerifierChanged += changes.Add;

        Eip8288Verifier? before = service.RefreshVerifier();
        head = HeadWithParent(finder, 10, 990);
        Eip8288Verifier? after = service.RefreshVerifier();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Is.EqualTo(Eip8288Verifier.Pinned));
            Assert.That(after, Is.EqualTo(new Eip8288Verifier(upgradedVk)));
            Assert.That(changes, Is.EqualTo(new[] { new Eip8288Verifier(upgradedVk) }));
            Assert.That(store.Covers(directTx), Is.True, "a re-validated direct witness is kept");
            Assert.That(store.Covers(aggregatedTx), Is.False, "every aggregate proof is dropped");
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1), "the direct verdict is reused under unchanged scheme parameters");
            pool.Received(1).RemoveTransaction(aggregatedTx.Hash);
            pool.DidNotReceive().RemoveTransaction(directTx.Hash);
            head = HeadWithParent(finder, 75, 1_810);
            Assert.That(service.RefreshVerifier(), Is.EqualTo(after));
            Assert.That(changes, Has.Count.EqualTo(1));
        }
    }

    // EIP-8288 target fork: the fork active at head.timestamp + L, with L = (head.timestamp - parent.timestamp) /
    // (head.slot_number - parent.slot_number) from the EIP-7843 slot numbers, so a missed slot or another slot length changes the
    // estimate and no configured slot length is used. When slot numbers do not increase the target fork is the head's fork, as
    // it is, defensively, without a parent slot number; L is floor division.
    [TestCase(9UL, 980UL, 990UL, 10UL, true, TestName = "Ten-second slots reach the fork")]
    [TestCase(9UL, 984UL, 990UL, 6UL, false, TestName = "Six-second slots stop short of the fork")]
    [TestCase(8UL, 970UL, 990UL, 10UL, true, TestName = "A missed slot does not stretch the estimate")]
    [TestCase(9UL, 978UL, 990UL, 12UL, true, TestName = "Twelve-second slots pass the fork")]
    [TestCase(null, 980UL, 990UL, null, false, TestName = "Without a parent slot number the head's fork is kept defensively")]
    [TestCase(10UL, 980UL, 990UL, null, false, TestName = "A slot number that does not increase gives the head's fork")]
    [TestCase(11UL, 980UL, 990UL, null, false, TestName = "A decreasing slot number gives the head's fork")]
    [TestCase(8UL, 971UL, 990UL, 9UL, false, TestName = "The estimate rounds down")]
    [TestCase(null, 990UL, 1_000UL, null, true, TestName = "Without a parent slot number a head on the fork keeps it defensively")]
    public void Target_fork_is_predicted_from_the_local_slot_length(ulong? parentSlot, ulong parentTimestamp, ulong headTimestamp,
        ulong? slotLength, bool upgraded)
    {
        ValueHash256 upgradedVk = ValueKeccak.Compute("next AGGREGATED_VK");
        CustomSpecProvider specs = new(((ForkActivation)0, Eip8288Prototype.Instance),
            (new ForkActivation(0, 1_000), new OverridableReleaseSpec(Eip8288Prototype.Instance) { Eip8288AggregatedVk = upgradedVk }));
        BlockHeader parent = Build.A.BlockHeader.WithNumber(9).WithTimestamp(parentTimestamp).WithSlotNumber(parentSlot).TestObject;
        BlockHeader head = Build.A.BlockHeader.WithParent(parent).WithNumber(10).WithTimestamp(headTimestamp).WithSlotNumber(10).TestObject;
        IBlockFinder finder = Substitute.For<IBlockFinder>();
        finder.FindHeader(parent.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        finder.Head.Returns(new Block(head));
        ProofWrapperService service = new(Substitute.For<ITxPool>(), specs, finder, new LeanProofStore(FrameTxSigHash.ComputeValue),
            new FakeLeanProofVerifier(true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(WrapperTargetFork.SlotLength(head, parent), Is.EqualTo(slotLength));
            Assert.That(service.TargetSpec?.Eip8288AggregatedVk, Is.EqualTo(upgraded ? upgradedVk : Eip8288Verifier.PinnedAggregatedVk));
        }
    }

    /// <summary>A head at <paramref name="number"/> and <paramref name="timestamp"/> whose parent, found through
    /// <paramref name="finder"/>, is one EIP-7843 slot of <paramref name="slotLength"/> seconds earlier.</summary>
    private static Block HeadWithParent(IBlockFinder finder, ulong number, ulong timestamp, ulong slotLength = 10)
    {
        BlockHeader parent = Build.A.BlockHeader.WithNumber(number - 1).WithTimestamp(timestamp - slotLength).WithSlotNumber(number - 1).TestObject;
        finder.FindHeader(parent.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(parent);
        return Build.A.Block.WithParent(parent).WithNumber(number).WithTimestamp(timestamp).WithSlotNumber(number).TestObject;
    }

    private static (ProofWrapperService, FakeLeanProofVerifier) Create(int count, int payloadBytes)
        => Create(count, payloadBytes, out _);

    private static (ProofWrapperService, FakeLeanProofVerifier) Create(int count, int payloadBytes, out Transaction[] transactions)
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, TestItem.KeccakA.ValueHash256, default);
        byte[] payload = new byte[payloadBytes];
        transactions = Enumerable.Range(0, count).Select(index => new Transaction
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
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        store.AddVerified([dependency], [[1]], null);
        FakeLeanProofVerifier verifier = new(true);
        return (CreateService(transactions, store, verifier), verifier);
    }

    private static ProofWrapperService CreateService(Transaction[] transactions, LeanProofStore store, ILeanProofVerifier verifier, IReleaseSpec? spec = null, ITxPool? pool = null)
    {
        pool ??= Substitute.For<ITxPool>();
        pool.GetPendingTransactions().Returns(transactions);
        ITxPool pendingPool = pool;
        pool.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(call =>
        {
            Transaction? found = Array.Find(pendingPool.GetPendingTransactions(), transaction => transaction.Hash!.ValueHash256 == call.Arg<ValueHash256>());
            call[1] = found;
            return found is not null;
        });
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
