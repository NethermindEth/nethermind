// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

public class Eip8288BlockProductionTests
{
    [Test]
    public async Task Background_aggregate_is_reused_by_fresh_production_scopes([Values] bool mixed)
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await CreateChain(verifier, proofs);
        FrameDependency firstDependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("background-first"), default);
        FrameDependency secondDependency = new(mixed ? Eip8288Constants.LeanStarkScheme : Eip8288Constants.LeanSphincsScheme,
            ValueKeccak.Compute("background-second"), default);
        Transaction first = CreateTransaction(chain, firstDependency, [1]);
        Transaction second = CreateTransaction(chain, secondDependency, [2]);
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new(first), new(second)],
            Deps = Eip8288Dependencies.Canonicalize([firstDependency, secondDependency]),
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1], [1]]
        }).Bytes;
        Assert.That((await service.AcceptAsync(wrapper)).IsSuccess, Is.True);
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        Block? built = await chain.BlockProducer.BuildBlock();
        Assert.That(built, Is.Not.Null);
        Assert.That(built!.Transactions, Has.Length.EqualTo(2));
        Assert.That(verifier.ProofCalls, Is.EqualTo(1), "the background proof already covers the final body");
        await using ScopedBlockProducerEnv environment = chain.Container.Resolve<IBlockProducerEnvFactory>().CreateTransient();
        BlockToProduce repeated = new(built.Header.Clone(), built.Transactions, [], built.Withdrawals);
        Block? processed = environment.ChainProcessor.Process(repeated, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);
        Assert.That(processed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed!.Transactions, Has.Length.EqualTo(2));
            Assert.That(processed.Header.RecursiveStark!.BlockDepsHash,
                Is.EqualTo(new Hash256(Eip8288Dependencies.ComputeBlockDepsHash(processed))));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1), "a fresh production scope shares the prepared statement");
        }
    }

    [Test]
    public async Task Rejected_optional_cache_proof_is_replaced_from_selected_witnesses()
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await CreateChain(verifier, proofs);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("rejected-cache"), default);
        Transaction transaction = CreateTransaction(chain, dependency, [UInt256.Zero]);
        proofs.AddVerified([dependency], [[1]], null);
        Assert.That(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        proofs.AddCachedRecursive([dependency], [0xee]);

        Block? block = await chain.BlockProducer.BuildBlock();

        Assert.That(block, Is.Not.Null);
        Assert.That(block!.Transactions, Has.Length.EqualTo(1));
        ValueHash256 hash = Eip8288Dependencies.ComputeBlockDepsHash(block);
        Assert.That(verifier.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, block.Header.RecursiveStark!.StarkProof), Is.True);
        Assert.That(verifier.ProofCalls, Is.EqualTo(1), "a rejected optional cache entry cannot stall fresh proving");
        Assert.That(proofs.TryGetRecursiveProof([dependency], out byte[]? replacement), Is.True);
        Assert.That(replacement, Is.EqualTo(block.Header.RecursiveStark.StarkProof));
        Block? repeated = await chain.BlockProducer.BuildBlock();
        Assert.That(repeated!.Header.RecursiveStark!.StarkProof, Is.EqualTo(replacement));
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Concurrent_identical_work_is_shared_and_cached_results_do_not_wait_for_unrelated_proving()
    {
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("shared-first"), default);
        FrameDependency second = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("shared-second"), default);
        ValueHash256 firstHash = Eip8288Dependencies.ComputeDepsHash([first]);
        ValueHash256 secondHash = Eip8288Dependencies.ComputeDepsHash([second]);
        AggregationInput firstInput = new() { Deps = [first], Witnesses = [new byte[] { 1 }] };
        AggregationInput secondInput = new() { Deps = [second], Witnesses = [new byte[] { 1 }] };
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        CountingVerifier verifier = new() { OnProof = () => { entered.Set(); release.Wait(); } };
        ProductionProofCache cache = new(verifier);
        Task<byte[]> initial = Task.Run(() => cache.ProveRecursiveStark(firstHash, Eip8288Constants.AggregatedVk, firstInput));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            using ManualResetEventSlim requested = new();
            Task<byte[]> concurrent = Task.Run(() =>
            {
                requested.Set();
                return cache.ProveRecursiveStark(firstHash, Eip8288Constants.AggregatedVk, firstInput);
            });
            Assert.That(requested.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(50);
            Assert.That(concurrent.IsCompleted, Is.False);
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
            release.Set();
            byte[][] completed = await Task.WhenAll(initial, concurrent).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(completed[0], Is.EqualTo(completed[1]));
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
            entered.Reset();
            release.Reset();
            Task<byte[]> unrelated = Task.Run(() => cache.ProveRecursiveStark(secondHash, Eip8288Constants.AggregatedVk, secondInput));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Task<byte[]> ready = Task.Run(() => cache.ProveRecursiveStark(firstHash, Eip8288Constants.AggregatedVk, firstInput));
            Assert.That(await ready.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(firstHash.ToByteArray()));
            Assert.That(unrelated.IsCompleted, Is.False);
            release.Set();
            await unrelated.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(verifier.ProofCalls, Is.EqualTo(2));
        }
        finally
        {
            release.Set();
            await initial.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Queued_requests_snapshot_the_statement_and_do_not_start_after_cancellation([Values] bool cancel)
    {
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("queued-first"), default);
        FrameDependency second = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("queued-second"), default);
        ValueHash256 firstHash = Eip8288Dependencies.ComputeDepsHash([first]);
        ValueHash256 secondHash = Eip8288Dependencies.ComputeDepsHash([second]);
        MutableStatement statement = new() { Hash = secondHash };
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim observed = new();
        using CancellationTokenSource cancellation = new();
        CountingVerifier verifier = new() { OnProof = () => { entered.Set(); release.Wait(); } };
        ProductionProofCache cache = new(verifier);
        Task<byte[]> active = Task.Run(() => cache.ProveRecursiveStark(firstHash, Eip8288Constants.AggregatedVk,
            new() { Deps = [first], Witnesses = [new byte[] { 1 }] }));
        Task<byte[]>? queued = null;
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            AggregationInput input = new() { Deps = new ObservedDependencies(second, observed), Witnesses = [new byte[] { 1 }] };
            queued = Task.Run(() => cancel
                ? ProductionProofCache.Prove(cache, secondHash, Eip8288Constants.AggregatedVk, input, cancellation.Token)
                : cache.ProveRecursiveStark(in statement.Hash, Eip8288Constants.AggregatedVk, input));
            Assert.That(observed.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(50);
            statement.Hash = firstHash;
            if (cancel) cancellation.Cancel();
            using (ProductionProofCache.Background(default))
            {
                Assert.Throws<InvalidOperationException>(() => cache.ProveRecursiveStark(secondHash, Eip8288Constants.AggregatedVk,
                    new() { Deps = [second], Witnesses = [new byte[] { 1 }] }));
            }
            Assert.That(verifier.ProofCalls, Is.EqualTo(1), "background work does not queue behind the active producer");
            if (cancel)
            {
                Assert.That(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
                Assert.That(active.IsCompleted, Is.False, "a canceled waiter returns while unrelated native work remains active");
            }
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
            {
                Assert.That(verifier.ProofCalls, Is.EqualTo(1));
            }
            else
            {
                Assert.That(await queued.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(secondHash.ToByteArray()));
                Assert.That(cache.ProveRecursiveStark(secondHash, Eip8288Constants.AggregatedVk,
                    new() { Deps = [second], Witnesses = [new byte[] { 1 }] }), Is.EqualTo(secondHash.ToByteArray()));
                Assert.That(verifier.ProofCalls, Is.EqualTo(2));
            }
        }
        finally
        {
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            if (queued is not null && !cancel) await queued.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class MutableStatement
    {
        public ValueHash256 Hash;
    }

    private sealed class ObservedDependencies(FrameDependency dependency, ManualResetEventSlim observed) : IReadOnlyList<FrameDependency>
    {
        public int Count { get { observed.Set(); return 1; } }
        public FrameDependency this[int index] => index == 0 ? dependency : throw new IndexOutOfRangeException();
        public IEnumerator<FrameDependency> GetEnumerator() => ((IEnumerable<FrameDependency>)(FrameDependency[])[dependency]).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Test]
    public async Task Improvement_passes_reuse_verified_proofs_and_changed_dependency_sets_replace_the_cache()
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier)
            .AddSingleton(proofs));
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        TxFrame[] frames =
        [
            FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        proofs.AddVerified([dependency], [[1]], null);
        Assert.That(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

        Block? first = await chain.BlockProducer.BuildBlock();
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Transactions, Has.Length.EqualTo(1));
        first.Header.RecursiveStark!.StarkProof[0] ^= 0xff;
        Block? second = await chain.BlockProducer.BuildBlock();
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Transactions, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.ProofCalls, Is.EqualTo(1));
            Assert.That(verifier.RecursiveVerificationCalls, Is.EqualTo(2));
            Assert.That(second.Header.RecursiveStark!.StarkProof,
                Is.EqualTo(Eip8288Dependencies.ComputeBlockDepsHash(second).ToByteArray()));
        }

        Block? empty = await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock);
        Assert.That(empty, Is.Not.Null);
        Assert.That(empty!.Transactions, Is.Empty);
        Assert.That(verifier.ProofCalls, Is.EqualTo(2));
        Block? restored = await chain.BlockProducer.BuildBlock();
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored!.Transactions, Has.Length.EqualTo(1));
        Assert.That(verifier.ProofCalls, Is.EqualTo(2));
    }

    [Test]
    public void Canceled_production_reuses_verified_completed_steps_without_finishing_the_canceled_tree()
    {
        FrameDependency[] dependencies = new FrameDependency[8];
        ReadOnlyMemory<byte>[] witnesses = new ReadOnlyMemory<byte>[8];
        for (int i = 0; i < dependencies.Length; i++)
        {
            dependencies[i] = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(i.ToString()), default);
            witnesses[i] = new byte[] { 1 };
        }
        AggregationInput input = new() { Deps = dependencies, Witnesses = witnesses };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        CountingVerifier verifier = new();
        ProductionProofCache cache = new(verifier);
        for (int i = 0; i < 3; i++)
        {
            using CancellationTokenSource canceled = new();
            verifier.OnProof = canceled.Cancel;
            Assert.Throws<OperationCanceledException>(() => RecursiveStarkAggregator.Prove(input, cache, hash, canceled.Token));
            Assert.That(verifier.ProofCalls, Is.EqualTo(i + 1));
        }
        verifier.OnProof = null;
        byte[] result = RecursiveStarkAggregator.Prove(input, cache, hash);
        result[0] ^= 0xff;
        byte[] reused = RecursiveStarkAggregator.Prove(input, cache, hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reused, Is.EqualTo(hash.ToByteArray()));
            Assert.That(verifier.ProofCalls, Is.EqualTo(3));
            Assert.That(verifier.RecursiveVerificationCalls, Is.EqualTo(3));
        }
    }

    [Test]
    public void Production_cache_rejects_wrong_statement_and_does_not_reuse_changed_inputs()
    {
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("cache-a"), default);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        CountingVerifier verifier = new() { WrongStatement = true };
        ProductionProofCache cache = new(verifier);
        AggregationInput input = new() { Deps = [dependency], Witnesses = [new byte[] { 1 }] };
        Assert.Throws<InvalidOperationException>(() => cache.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input));
        verifier.WrongStatement = false;
        Assert.That(cache.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input), Is.EqualTo(hash.ToByteArray()));
        Assert.That(verifier.ProofCalls, Is.EqualTo(2));
        verifier.RejectChangedWitness = true;
        AggregationInput bad = new() { Deps = [dependency], Witnesses = [new byte[] { 2 }] };
        Assert.Throws<InvalidOperationException>(() => cache.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, bad));
        Assert.That(verifier.ProofCalls, Is.EqualTo(3));
        FrameDependency changed = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("cache-b"), default);
        ValueHash256 changedHash = Eip8288Dependencies.ComputeDepsHash([changed]);
        AggregationInput next = new() { Deps = [changed], Witnesses = [new byte[] { 1 }] };
        Assert.That(cache.ProveRecursiveStark(changedHash, Eip8288Constants.AggregatedVk, next), Is.EqualTo(changedHash.ToByteArray()));
        Assert.That(verifier.ProofCalls, Is.EqualTo(4));
    }

    [Test]
    public void Completed_proving_scopes_revoke_captured_cancellation([Values] bool background)
    {
        using CancellationTokenSource cancellation = new();
        ExecutionContext? captured = null;
        CountingVerifier verifier = new();
        ProductionProofCache cache = new(verifier);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("scope"), default);
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        AggregationInput input = new() { Deps = [dependency], Witnesses = [new byte[] { 1 }] };
        if (background)
        {
            using (ProductionProofCache.Background(cancellation.Token)) captured = ExecutionContext.Capture();
        }
        else
        {
            verifier.OnProof = () => captured = ExecutionContext.Capture();
            ProductionProofCache.Prove(cache, hash, Eip8288Constants.AggregatedVk, input, cancellation.Token);
            verifier.OnProof = null;
        }
        cancellation.Cancel();
        Assert.That(captured, Is.Not.Null);
        ExecutionContext.Run(captured!, _ =>
            Assert.That(cache.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input), Is.EqualTo(hash.ToByteArray())), null);
    }

    [Test]
    public void Production_step_cache_releases_old_proofs_under_byte_pressure()
    {
        CountingVerifier verifier = new() { ProofPaddingBytes = Eip8288Constants.MaxProofBytes - 32 };
        ProductionProofCache cache = new(verifier);
        FrameDependency first = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("large-0"), default);
        AggregationInput initial = new() { Deps = [first], Witnesses = [new byte[] { 1 }] };
        ValueHash256 initialHash = Eip8288Dependencies.ComputeDepsHash([first]);
        cache.ProveRecursiveStark(initialHash, Eip8288Constants.AggregatedVk, initial);
        for (int i = 1; i <= 4; i++)
        {
            FrameDependency next = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"large-{i}"), default);
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([next]);
            cache.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk,
                new() { Deps = [next], Witnesses = [new byte[] { 1 }] });
        }
        Assert.That(cache.ProveRecursiveStark(initialHash, Eip8288Constants.AggregatedVk, initial), Has.Length.EqualTo(Eip8288Constants.MaxProofBytes));
        Assert.That(verifier.ProofCalls, Is.EqualTo(6));
    }

    [Test]
    public async Task Pool_membership_pins_witnesses_through_flood_replacement_and_removal()
    {
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await CreateChain(new CountingVerifier(), proofs);
        FrameDependency a = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("pending-a"), default);
        FrameDependency b = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("pending-b"), default);
        Transaction first = CreateTransaction(chain, a, [UInt256.Zero]);
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        Assert.That((await service.AcceptAsync(EncodeWrapper(a, first))).IsSuccess, Is.True);
        Flood();
        Assert.That(proofs.Covers(first), Is.True);
        Transaction replacement = CreateTransaction(chain, b, [UInt256.Zero]);
        replacement.GasPrice = 2.GWei;
        replacement.DecodedMaxFeePerGas = 200.GWei;
        FrameTxTestFrames.SignSecp256k1(replacement, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        replacement.Hash = replacement.CalculateHash();
        Assert.That((await service.AcceptAsync(EncodeWrapper(b, replacement))).IsSuccess, Is.True);
        Flood();
        Assert.That(chain.TxPool.TryGetPendingTransaction(first.Hash!.ValueHash256, out _), Is.False);
        Assert.That(proofs.Covers(first), Is.False);
        Assert.That(proofs.Covers(replacement), Is.True);
        Assert.That(chain.TxPool.RemoveTransaction(replacement.Hash), Is.True);
        Flood();
        Assert.That(proofs.Covers(replacement), Is.False);
        FrameDependency c = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("shutdown"), default);
        FrameDependency unrelated = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("other-owner"), default);
        Transaction shuttingDown = CreateTransaction(chain, c, [UInt256.Zero]);
        Transaction otherOwner = CreateTransaction(chain, unrelated, [UInt256.Zero]);
        proofs.AddVerified([unrelated], [[1]], null);
        proofs.PinPending(otherOwner);
        Assert.That((await service.AcceptAsync(EncodeWrapper(c, shuttingDown))).IsSuccess, Is.True);
        await ((IAsyncDisposable)chain.TxPool).DisposeAsync();
        Flood();
        Assert.That(proofs.Covers(shuttingDown), Is.False);
        Assert.That(proofs.Covers(otherOwner), Is.True, "shutdown releases only this pool's witness pins");


        void Flood()
        {
            byte[] witness = new byte[64 * 1024];
            for (int i = 0; i < 1100; i++)
                proofs.AddVerified([new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"flood:{i}"), default)], [witness], null);
        }
    }

    [Test]
    public async Task Cancelled_proving_does_not_publish_but_retains_verified_work_for_retry()
    {
        using CancellationTokenSource cancellation = new();
        CountingVerifier verifier = new() { OnProof = cancellation.Cancel };
        using BasicTestBlockchain chain = await CreateChain(verifier, new LeanProofStore());
        Hash256 head = chain.BlockTree.Head!.Hash!;
        Assert.That(async () => await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock, cancellationToken: cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        Assert.That(chain.BlockTree.Head.Hash, Is.EqualTo(head));
        verifier.OnProof = null;
        Block? fresh = await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock);
        Assert.That(fresh, Is.Not.Null);
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Plain_producing_blocks_skip_uncovered_transactions_before_execution()
    {
        CountingVerifier verifier = new();
        using BasicTestBlockchain chain = await CreateChain(verifier, new LeanProofStore());
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = CreateTransaction(chain, dependency, [UInt256.Zero]);
        Block? template = await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock);
        Assert.That(template, Is.Not.Null);
        Block plain = new(template!.Header, [transaction], [], template.Withdrawals);
        await using ScopedBlockProducerEnv environment = chain.Container.Resolve<IBlockProducerEnvFactory>().CreateTransient();
        Block? processed = environment.ChainProcessor.Process(plain, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);
        Assert.That(processed, Is.Not.Null);
        Assert.That(processed!.Transactions, Is.Empty);
        Assert.That(Eip8288Dependencies.ForBlock(processed), Is.Empty);
        Assert.That(processed.Header.RecursiveStark!.StarkProof, Is.EqualTo(Eip8288Dependencies.ComputeBlockDepsHash(processed).ToByteArray()));
    }

    [Test]
    public async Task Owned_inclusion_list_witnesses_produce_when_the_shared_store_is_full()
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await CreateChain(verifier, proofs);
        byte[] witness = new byte[64 * 1024 - Eip8288Constants.DependencyTripleLength];
        for (int i = 0; i < 1024; i++)
        {
            FrameDependency cached = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"cached:{i}"), default);
            proofs.AddVerified([cached], [witness], null);
            Assert.That(proofs.PinPending(new Transaction
            {
                Type = TxType.FrameTx,
                Hash = new Hash256(cached.DataHash),
                SenderAddress = new Address(cached.DataHash.Bytes[..20]),
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([cached]))]
            }), Is.True);
        }
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("inclusion-list"), default);
        proofs.AddVerified([dependency], null, [1]);
        Transaction transaction = CreateTransaction(chain, dependency, [UInt256.Zero]);
        Assert.That(proofs.Covers(transaction), Is.False);
        Block? template = await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock);
        BlockToProduce producing = new(template!.Header, [transaction], [], template.Withdrawals)
        {
            InclusionListProofInput = new() { RecursiveProofs = [new([dependency], Eip8288Dependencies.ComputeDepsHash([dependency]).ToByteArray())] }
        };
        int proofCallsBefore = verifier.ProofCalls;
        int verificationCallsBefore = verifier.RecursiveVerificationCalls;
        await using ScopedBlockProducerEnv environment = chain.Container.Resolve<IBlockProducerEnvFactory>().CreateTransient();
        Block? processed = environment.ChainProcessor.Process(producing, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);
        Assert.That(processed, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed!.Transactions, Is.EqualTo((Transaction[])[transaction]));
            Assert.That(Eip8288Dependencies.ForBlock(processed), Is.EqualTo((FrameDependency[])[dependency]));
            Assert.That(processed.Header.RecursiveStark!.BlockDepsHash, Is.EqualTo(new Hash256(Eip8288Dependencies.ComputeDepsHash([dependency]))));
            Assert.That(processed.Header.RecursiveStark.StarkProof, Is.EqualTo(producing.InclusionListProofInput!.RecursiveProofs[0].Proof.ToArray()));
            Assert.That(verifier.ProofCalls, Is.EqualTo(proofCallsBefore), "the unchanged verified IL statement needs no new proof");
            Assert.That(verifier.RecursiveVerificationCalls, Is.GreaterThan(verificationCallsBefore));
            Assert.That(proofs.Covers(transaction), Is.False, "production owns the verified IL witness without evicting pending pool coverage");
        }
    }

    [Test]
    public async Task Proof_wrappers_preserve_independent_keyed_nonce_domains_through_production()
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await CreateChain(verifier, proofs);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction first = CreateTransaction(chain, dependency, [1, 7]);
        Transaction independent = CreateTransaction(chain, dependency, [9]);
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        Assert.That((await service.AcceptAsync(EncodeWrapper(dependency, first, independent))).IsSuccess, Is.True);
        Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        Block block = await ProduceAndImport(chain, slot: 1);
        Assert.That(block.Transactions, Has.Length.EqualTo(2));
        using (chain.MainWorldState.BeginScope(block.Header))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.MainWorldState.GetNonce(TestItem.PrivateKeyB.Address), Is.Zero);
            foreach (UInt256 key in (UInt256[])[1, 7, 9])
                Assert.That(KeyedNonceManager.CurrentNonceSeq(chain.MainWorldState, TestItem.PrivateKeyB.Address, key), Is.EqualTo(1));
        }
        Transaction replay = CreateTransaction(chain, dependency, [1]);
        Result<Hash256[]> result = await service.AcceptAsync(EncodeWrapper(dependency, replay));
        Assert.That(result.IsSuccess, Is.False);
    }

    [TestCase(true, 1UL, 1)]
    [TestCase(true, 8192UL, 0)]
    [TestCase(false, 1UL, 0)]
    public async Task Proof_wrappers_enforce_recent_root_commitments_and_execution_slot_age(bool matchingRoot, ulong slot, int included)
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        ValueHash256 sourceId = RecentRootStore.SourceId(TestItem.AddressD, TestItem.KeccakA.ValueHash256);
        ValueHash256 root = TestItem.KeccakB.ValueHash256;
        using BasicTestBlockchain chain = await CreateChain(verifier, proofs, builder =>
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((state, spec) => new FunctionalGenesisPostProcessor(_ =>
            {
                state.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero, nonce: 1);
                state.Set(RecentRootStore.ReferenceCell(sourceId, 0), RecentRootStore.EntryHash(sourceId, 0, root).ToUInt256());
                state.CreateAccount(TestItem.AddressD, UInt256.Zero);
                state.InsertCode(TestItem.AddressD, Prepare.EvmCode.PushData(0).PushData(2).Op(Instruction.RECENTROOTREFLOAD)
                    .PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done, spec.GenesisSpec);
                state.RecalculateStateRoot();
            })));
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = CreateTransaction(chain, dependency, [UInt256.Zero],
            [new(sourceId, 0, matchingRoot ? root : TestItem.KeccakC.ValueHash256)],
            new(FrameMode.Sender, FrameFlags.None, TestItem.AddressD, executionGasLimit: 100_000, stateGasLimit: GasCostOf.SSetState, UInt256.Zero, default),
            FrameTxTestFrames.PostTx(10_000));
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        Result<Hash256[]> accepted = await service.AcceptAsync(EncodeWrapper(dependency, transaction));
        Assert.That(accepted.IsSuccess, Is.EqualTo(matchingRoot));
        Assert.That(verifier.VerificationCalls, Is.EqualTo(matchingRoot ? 1 : 0));
        if (matchingRoot) Assert.That(service.BuildWrapper().IsSuccess, Is.True);
        if (!matchingRoot)
        {
            Assert.That(proofs.TryBeginAdmission([dependency], [[1]], null, out IDisposable? admission), Is.True);
            using (admission)
                Assert.That(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.FrameTxRecentRootUnmet));
        }
        Block block = await ProduceAndImport(chain, slot);
        Assert.That(block.Transactions, Has.Length.EqualTo(included));
        using (chain.MainWorldState.BeginScope(block.Header))
        {
            chain.MainWorldState.Get(new StorageCell(TestItem.AddressD, UInt256.Zero), out UInt256 stored);
            Assert.That(stored, Is.EqualTo(included == 1 ? root.ToUInt256() : UInt256.Zero));
        }
    }

    [Test]
    public async Task Expired_recent_roots_are_rejected_before_native_wrapper_verification()
    {
        CountingVerifier verifier = new();
        LeanProofStore proofs = new();
        ValueHash256 source = TestItem.KeccakA.ValueHash256;
        ValueHash256 root = TestItem.KeccakB.ValueHash256;
        using BasicTestBlockchain chain = await CreateChain(verifier, proofs, builder =>
            builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((state, spec) => new FunctionalGenesisPostProcessor(_ =>
            {
                state.CreateAccount(Eip8272Constants.RecentRootAddress, UInt256.Zero, nonce: 1);
                state.Set(RecentRootStore.ReferenceCell(source, 0), RecentRootStore.EntryHash(source, 0, root).ToUInt256());
            })));
        await ProduceAndImport(chain, 8191);
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, default, default);
        Transaction transaction = CreateTransaction(chain, dependency, [UInt256.Zero], [new(source, 0, root)]);
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        Result<Hash256[]> accepted = await service.AcceptAsync(EncodeWrapper(dependency, transaction));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted.IsSuccess, Is.False);
            Assert.That(verifier.VerificationCalls, Is.Zero);
            Assert.That(chain.TxPool.GetPendingTransactionsCount(), Is.Zero);
        }
    }

    private static Task<BasicTestBlockchain> CreateChain(CountingVerifier verifier, LeanProofStore proofs, Action<ContainerBuilder>? configure = null) =>
        BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance));
            builder.AddSingleton<ILeanProofVerifier>(verifier);
            builder.AddSingleton(proofs);
            builder.ConfigureTestConfiguration(configuration => configuration.AddBlockOnStart = false);
            configure?.Invoke(builder);
        });

    private static Transaction CreateTransaction(BasicTestBlockchain chain, FrameDependency dependency, UInt256[] keys,
        RecentRootReference[]? roots = null, params TxFrame[] execution)
    {
        ulong stateGas = keys.Length == 1 && keys[0].IsZero ? 0 : (ulong)keys.Length * GasCostOf.SSetState;
        TxFrame[] frames =
        [
            new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, executionGasLimit: FrameTxTestFrames.PrefixFrameGas,
                stateGasLimit: stateGas, UInt256.Zero, default),
            new(FrameMode.DepVerify, FrameFlags.None, null, dependency.Scheme == Eip8288Constants.LeanStarkScheme
                ? Eip8288Constants.LeanStarkVerificationGas : Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency])),
            .. execution
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            NonceKeys = keys,
            RecentRootReferences = roots,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    private static byte[] EncodeWrapper(FrameDependency dependency, params Transaction[] transactions) => MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
    {
        Transactions = Array.ConvertAll(transactions, static transaction => new WrapperTransaction(transaction)),
        Deps = [dependency],
        Mode = MempoolWrapper.ModeDirect,
        Proofs = [[1]]
    }).Bytes;

    private static async Task<Block> ProduceAndImport(BasicTestBlockchain chain, ulong slot)
    {
        Block? block = await chain.BlockProducer.BuildBlock(payloadAttributes: new PayloadAttributes
        {
            Timestamp = chain.BlockTree.Head!.Timestamp + 1,
            SlotNumber = slot
        });
        Assert.That(block, Is.Not.Null);
        Task imported = chain.WaitForNewHeadWhere(added => added.Hash == block!.Hash);
        Assert.That(chain.BlockTree.SuggestBlock(block!), Is.EqualTo(AddBlockResult.Added));
        await imported;
        return block!;
    }

    private sealed class CountingVerifier : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public int ProofCalls { get; private set; }
        public bool WrongStatement { get; set; }
        public bool RejectChangedWitness { get; set; }
        public int ProofPaddingBytes { get; set; }
        public Action? OnProof { get; set; }
        public int RecursiveVerificationCalls { get; private set; }
        public int VerificationCalls { get; private set; }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        {
            VerificationCalls++;
            return true;
        }
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
        {
            RecursiveVerificationCalls++;
            return proof.Length == 32 + ProofPaddingBytes && proof[..32].SequenceEqual(depsHash.Bytes);
        }
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            OnProof?.Invoke();
            if (RejectChangedWitness)
                foreach (ReadOnlyMemory<byte> witness in input.Witnesses)
                    if (witness.Length != 1 || witness.Span[0] != 1)
                        throw new InvalidOperationException("Invalid witness");
            byte[] proof = new byte[32 + ProofPaddingBytes];
            if (!WrongStatement) depsHash.Bytes.CopyTo(proof);
            return proof;
        }
    }
}
