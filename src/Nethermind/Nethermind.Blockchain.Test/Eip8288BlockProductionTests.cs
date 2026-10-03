// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Eip8288;
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
        Assert.That(verifier.ProofCalls, Is.EqualTo(3));
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
            for (int i = 0; i < 1100; i++)
                proofs.AddVerified([new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"flood:{i}"), default)], [[1]], null);
        }
    }

    [Test]
    public async Task Cancelled_proving_does_not_publish_or_cache_the_proof()
    {
        using CancellationTokenSource cancellation = new();
        CountingVerifier verifier = new() { OnProof = cancellation.Cancel };
        using BasicTestBlockchain chain = await CreateChain(verifier, new LeanProofStore());
        Assert.That(async () => await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock, cancellationToken: cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(verifier.ProofCalls, Is.EqualTo(1));
        verifier.OnProof = null;
        Block? fresh = await chain.BlockProducer.BuildBlock(flags: IBlockProducer.Flags.EmptyBlock);
        Assert.That(fresh, Is.Not.Null);
        Assert.That(verifier.ProofCalls, Is.EqualTo(2));
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
        for (int i = 0; i < 1024; i++)
        {
            FrameDependency cached = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute($"cached:{i}"), default);
            proofs.AddVerified([cached], [[1]], null);
            proofs.PinPending(new Transaction
            {
                Type = TxType.FrameTx,
                Hash = new Hash256(cached.DataHash),
                Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                    UInt256.Zero, Eip8288Dependencies.Serialize([cached]))]
            });
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
        await using ScopedBlockProducerEnv environment = chain.Container.Resolve<IBlockProducerEnvFactory>().CreateTransient();
        Block? processed = environment.ChainProcessor.Process(producing, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance);
        Assert.That(processed, Is.Not.Null);
        Assert.That(processed!.Transactions, Has.Length.EqualTo(1));
        Assert.That(verifier.LastInput!.RecursiveProofs, Has.Count.EqualTo(1));
        Assert.That(proofs.Covers(transaction), Is.False, "production owns the verified IL witness without evicting pending pool coverage");
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
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
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
        public Action? OnProof { get; set; }
        public AggregationInput? LastInput { get; private set; }
        public int VerificationCalls { get; private set; }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
        {
            VerificationCalls++;
            return true;
        }
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof.SequenceEqual(depsHash.Bytes);
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            LastInput = input;
            OnProof?.Invoke();
            return depsHash.ToByteArray();
        }
    }
}
