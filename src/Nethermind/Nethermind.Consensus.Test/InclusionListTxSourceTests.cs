// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Test.ProofAggregation;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;


namespace Nethermind.Consensus.Test;

public class InclusionListTxSourceTests
{
    private static InclusionListTxSource CreateSource() => new(
        new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
        new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
        LimboLogs.Instance);

    private static PayloadAttributes Attributes(byte[][] inclusionList) => new() { InclusionListTransactions = inclusionList };

    private static IEnumerable<Transaction> GetTransactions(
        InclusionListTxSource source,
        PayloadAttributes payloadAttributes = null) =>
        source.GetTransactions(
            Build.A.BlockHeader.WithNumber(0).TestObject,
            Build.A.BlockHeader.WithNumber(1).TestObject,
            30_000_000UL,
            payloadAttributes);

    [TestCase(0UL, 1UL, true)]
    [TestCase(4UL, 11UL, true)]
    [TestCase(4UL, 4UL, false)]
    [TestCase(11UL, 4UL, false)]
    public void Lists_require_strict_committee_position_order(ulong first, ulong second, bool valid)
    {
        InclusionListV1[] lists =
        [
            new() { CommitteeIndex = first, Transactions = [] },
            new() { CommitteeIndex = second, Transactions = [] }
        ];
        string error = InclusionListV1.Validate(lists);
        Assert.That(error is null, Is.EqualTo(valid));
        Assert.That(lists[0].CommitteeIndex, Is.EqualTo(first));
        Assert.That(lists[1].CommitteeIndex, Is.EqualTo(second));
    }

    [Test]
    public void Legacy_source_preserves_list_and_proof_obligations_without_claiming_verified_coverage()
    {
        LegacyInclusionListTxSource legacy = new();
        IInclusionListTxSource source = legacy;
        Transaction transaction = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        PayloadAttributes attributes = new()
        {
            InclusionLists = [new InclusionListV1
            {
                CommitteeIndex = 5,
                Transactions = [Encode(transaction)],
                RecursiveStark = new RecursiveStarkV1 { StarkProofHash = TestItem.KeccakA, DepsHash = TestItem.KeccakB }
            }]
        };
        byte[][] list = attributes.CombinedInclusionListTransactions!;

        source.Set(list, Bogota.Instance, attributes.InclusionLists);
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(block, attributes);

        Assert.That(legacy.List, Is.SameAs(list));
        Assert.That(block.InclusionListTransactions.Single().Nonce, Is.EqualTo(transaction.Nonce));
        Assert.That(block.InclusionLists, Has.Count.EqualTo(1));
        Assert.That(block.InclusionLists[0].CommitteeIndex, Is.EqualTo(5));
        Assert.That(block.InclusionLists[0].Transactions, Is.EqualTo(block.InclusionListTransactions));
        Assert.That(block.InclusionLists[0].Commitment, Is.EqualTo(new InclusionListCommitment(TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256)));
        Assert.That(block.InclusionListProofInput, Is.Null);
    }

    [Test]
    public void Legacy_fallback_replaces_metadata_and_clears_unverified_coverage([Values("counted", "flattened", "absent")] string mode)
    {
        IInclusionListTxSource source = new LegacyInclusionListTxSource();
        Transaction transaction = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[] encoded = Encode(transaction);
        PayloadAttributes attributes = mode switch
        {
            "counted" => new() { InclusionLists = [new InclusionListV1 { CommitteeIndex = 5, Transactions = [encoded] }] },
            "flattened" => new() { InclusionListTransactions = [encoded] },
            _ => null
        };
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).TestObject)
        {
            InclusionListTransactions = [transaction],
            InclusionLists = [new CommitteeInclusionList(0, [transaction], null)],
            InclusionListProofInput = new AggregationInput()
        };

        source.ApplyInclusionList(block, attributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.InclusionListTransactions?.Length ?? 0, Is.EqualTo(mode == "absent" ? 0 : 1));
            Assert.That(block.InclusionLists?.Count ?? 0, Is.EqualTo(mode == "counted" ? 1 : 0));
            Assert.That(block.InclusionListProofInput, Is.Null);
        }
    }

    [Test]
    public void Payload_improvements_reuse_one_verified_snapshot()
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        FakeLeanProofVerifier verifier = new(true);
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        InclusionListTxSource source = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new TestSingleReleaseSpecProvider(spec), LimboLogs.Instance,
            new InclusionListProofService(new TestSingleReleaseSpecProvider(spec), verifier, store, Timestamper.Default));
        Transaction transaction = FrameTx(0, null!);
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = Eip8288Constants.LeanSphincsScheme;
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)];
        transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
        RecursiveStarkV1 commitment = Hold(store, verifier, [1], deps);
        PayloadAttributes attributes = new() { InclusionLists = [new InclusionListV1 { Transactions = [Encode(transaction)], RecursiveStark = commitment }] };
        byte[][] list = attributes.CombinedInclusionListTransactions!;
        source.Set(list, spec, attributes.InclusionLists);
        source.Set(list, spec, attributes.InclusionLists);
        // Holding the wrapper verified its proof once; the build checks the commitment by lookup.
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        Assert.That(GetTransactions(source, attributes).Single().Nonce, Is.Zero);
        BlockToProduce firstBuild = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(firstBuild, attributes);
        BlockToProduce nextBuild = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(nextBuild, attributes);
        Assert.That(nextBuild.InclusionListTransactions, Is.SameAs(firstBuild.InclusionListTransactions));
        Assert.That(nextBuild.InclusionListProofInput, Is.SameAs(firstBuild.InclusionListProofInput));
        source.Set(list, spec, attributes.InclusionLists);
        // The registered build owns its snapshot even if the caller mutates the encoded list.
        list[0][0] = 0xff;
        for (int improvement = 0; improvement < 12; improvement++)
            Assert.That(GetTransactions(source, attributes).Single().Nonce, Is.Zero);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        Assert.That(store.Covers(transaction), Is.True);
        commitment.DepsHash = TestItem.KeccakA;
        Assert.That(GetTransactions(source, attributes), Is.Empty, "a changed commitment is another build input");
    }

    // EIPs#12475: the build folds in each committed proof it holds, which may cover a superset of its list; a commitment to a
    // proof the builder does not hold is treated as null, obligating only that list's dependency-free entries.
    [Test]
    public void Each_list_proof_is_checked_and_aggregated_separately()
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        FakeLeanProofVerifier verifier = new(true);
        LeanProofStore store = new(FrameTxSigHash.ComputeValue);
        InclusionListTxSource source = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new TestSingleReleaseSpecProvider(spec), LimboLogs.Instance,
            new InclusionListProofService(new TestSingleReleaseSpecProvider(spec), verifier, store, Timestamper.Default));
        static Transaction DependencyTx(ulong nonce, string message)
        {
            Transaction transaction = FrameTx(nonce, null!);
            byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
            data[31] = Eip8288Constants.LeanSphincsScheme;
            Keccak.Compute(message).Bytes.CopyTo(data.AsSpan(32));
            transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)];
            transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
            return transaction;
        }
        Transaction unproven = DependencyTx(0, "unproven");
        Transaction proven = DependencyTx(1, "proven");
        // A mempool wrapper's proof covering the list and one more dependency.
        List<FrameDependency> wrapperDeps = Eip8288Dependencies.Canonicalize([.. Eip8288Dependencies.ForTransaction(proven, FrameTxSigHash.ComputeValue),
            new FrameDependency(Eip8288Constants.LeanSphincsScheme, Keccak.Compute("surplus").ValueHash256, default)]);
        PayloadAttributes attributes = new()
        {
            InclusionLists =
            [
                // A committed proof this builder does not hold is neither aggregated nor obligating.
                new InclusionListV1
                {
                    CommitteeIndex = 4,
                    Transactions = [Encode(unproven)],
                    RecursiveStark = new RecursiveStarkV1 { StarkProofHash = Keccak.Compute([1]), DepsHash = TestItem.KeccakA }
                },
                new InclusionListV1 { CommitteeIndex = 11, Transactions = [Encode(proven)], RecursiveStark = Hold(store, verifier, [2], wrapperDeps) }
            ]
        };
        source.Set(attributes.CombinedInclusionListTransactions!, spec, attributes.InclusionLists);
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(block, attributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(GetTransactions(source, attributes).Select(static tx => tx.Nonce), Is.EqualTo(new ulong[] { 1 }));
            Assert.That(block.InclusionListTransactions, Has.Length.EqualTo(2));
            Assert.That(block.InclusionLists!.Select(static list => list.CommitteeIndex), Is.EqualTo(new ulong[] { 4, 11 }));
            Assert.That(block.InclusionListProofInput!.RecursiveProofs, Has.Count.EqualTo(1));
            Assert.That(block.InclusionListProofInput.RecursiveProofs[0].InnerDeps, Is.EqualTo(wrapperDeps), "the whole superset; the block discards the surplus");
            Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Malformed_dependency_frame_is_rejected_before_proof_verification()
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        FakeLeanProofVerifier verifier = new(true);
        InclusionListTxSource source = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new TestSingleReleaseSpecProvider(spec), LimboLogs.Instance,
            new InclusionListProofService(new TestSingleReleaseSpecProvider(spec), verifier, new LeanProofStore(FrameTxSigHash.ComputeValue), Timestamper.Default));
        Transaction transaction = FrameTx(0, null!);
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[96])];
        PayloadAttributes attributes = new()
        {
            InclusionLists = [new InclusionListV1
            {
                Transactions = [Encode(transaction)],
                RecursiveStark = new RecursiveStarkV1 { StarkProofHash = Keccak.Compute([1]), DepsHash = TestItem.KeccakA }
            }]
        };
        byte[][] list = attributes.CombinedInclusionListTransactions!;
        source.Set(list, spec, attributes.InclusionLists);
        Assert.That(GetTransactions(source, attributes), Is.Empty);
        Assert.That(verifier.VerificationCalls, Is.Zero);
    }

    [Test]
    public void Empty_when_no_payload_attributes()
    {
        InclusionListTxSource source = CreateSource();

        Assert.That(GetTransactions(source), Is.Empty);
    }

    [Test]
    public void Empty_until_Set_is_called_for_the_build()
    {
        InclusionListTxSource source = CreateSource();
        Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(tx)];
        PayloadAttributes attrs = Attributes(il);

        Assert.That(GetTransactions(source, attrs), Is.Empty);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EqualTo([1ul]));
    }

    // Scoped by PayloadAttributes, so a concurrent FCU can't leak another build's IL.
    [Test]
    public void Inclusion_list_is_scoped_per_build()
    {
        InclusionListTxSource source = CreateSource();
        Transaction txA = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction txB = Build.A.Transaction.WithNonce(2).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] ilA = [Encode(txA)];
        byte[][] ilB = [Encode(txB)];
        PayloadAttributes attrsA = Attributes(ilA);
        PayloadAttributes attrsB = Attributes(ilB);

        // Build A supplies its IL, then build B supplies another before A consumes its list.
        source.Set(ilA, Bogota.Instance);
        source.Set(ilB, Bogota.Instance);

        Assert.That(GetTransactions(source, attrsA).Select(t => t.Nonce), Is.EqualTo([1ul]));
        Assert.That(GetTransactions(source, attrsB).Select(t => t.Nonce), Is.EqualTo([2ul]));
    }

    [Test]
    public void Set_with_empty_array_yields_empty()
    {
        InclusionListTxSource source = CreateSource();
        byte[][] il = [];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(GetTransactions(source, attrs), Is.Empty);
    }

    // Decoding and sender recovery must stay off the engine thread: a forkchoice update that is about to be
    // rejected, or that duplicates a build already under way, must cost nothing beyond retaining the list.
    [Test]
    public void Set_defers_sender_recovery_to_the_first_request()
    {
        CountingEcdsa ecdsa = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId));
        InclusionListTxSource source = new(ecdsa, new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)), LimboLogs.Instance);
        Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(tx)];

        source.Set(il, Bogota.Instance);
        Assert.That(ecdsa.Recoveries, Is.Zero);

        Assert.That(GetTransactions(source, Attributes(il)), Is.Not.Empty);
        Assert.That(ecdsa.Recoveries, Is.EqualTo(1));
    }

    // Per spec, blob (EIP-4844) transactions are excluded from the inclusion list.
    [Test]
    public void SupportsBlobs_is_false() => Assert.That(CreateSource().SupportsBlobs, Is.False);

    // A blob IL entry must be dropped, never forwarded into block production.
    [Test]
    public void Blob_transactions_are_filtered_out()
    {
        InclusionListTxSource source = CreateSource();
        Transaction normal = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction blob = Build.A.Transaction
            .WithType(TxType.Blob)
            .WithGasLimit(100_000)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithMaxFeePerBlobGas(10.GWei)
            .WithBlobVersionedHashes(1)
            .WithChainId(MainnetSpecProvider.Instance.ChainId)
            .WithNonce(2)
            .WithValue(UInt256.One)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[][] il = [Encode(normal), Encode(blob)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EqualTo([1ul]));
    }

    // A blob-carrying EIP-8141 frame transaction is type 6, so the type-3-only SupportsBlobs does not see it,
    // yet it reaches production without a sidecar just the same. The blob-free frame transaction is the
    // negative control: the filter must drop entries for carrying blobs, not for being frame transactions.
    [Test]
    public void Blob_carrying_frame_transactions_are_filtered_out()
    {
        InclusionListTxSource source = CreateSource();
        Transaction normal = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction blobFrameTx = FrameTx(nonce: 2, blobVersionedHashes: [new byte[32]]);
        Transaction plainFrameTx = FrameTx(nonce: 3, blobVersionedHashes: null!);
        byte[][] il = [Encode(normal), Encode(blobFrameTx), Encode(plainFrameTx)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EquivalentTo([1ul, 3ul]));
    }

    private static Transaction FrameTx(ulong nonce, byte[][] blobVersionedHashes) => new()
    {
        Type = TxType.FrameTx,
        ChainId = MainnetSpecProvider.Instance.ChainId,
        Nonce = nonce,
        SenderAddress = TestItem.AddressC,
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)],
        FrameSignatures = [],
        GasPrice = 1.GWei,
        DecodedMaxFeePerGas = 30.GWei,
        MaxFeePerBlobGas = blobVersionedHashes is null ? null : 1,
        BlobVersionedHashes = blobVersionedHashes,
    };

    // The producer offers each IL tx once, so a shuffled list must still come out in ascending nonce order.
    [Test]
    public void Sender_nonces_are_ordered_ascending()
    {
        InclusionListTxSource source = CreateSource();
        Transaction nonce1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction nonce0 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] il = [Encode(nonce1), Encode(nonce0)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => t.Nonce),
            Is.EqualTo([0ul, 1ul]));
    }

    // First-appearance order: sorting by address would favour low-address senders on a truncated list.
    [Test]
    public void Sender_order_of_first_appearance_is_preserved()
    {
        InclusionListTxSource source = CreateSource();
        Transaction b1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction a1 = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Transaction b0 = Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] il = [Encode(b1), Encode(a1), Encode(b0)];
        PayloadAttributes attrs = Attributes(il);

        source.Set(il, Bogota.Instance);
        Assert.That(
            GetTransactions(source, attrs).Select(t => (t.SenderAddress, t.Nonce)),
            Is.EqualTo([
                (TestItem.AddressB, 0ul),
                (TestItem.AddressB, 1ul),
                (TestItem.AddressA, 1ul)
            ]));
    }

    private static byte[] Encode(Transaction tx) => TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

    private sealed class LegacyInclusionListTxSource : IInclusionListTxSource
    {
        public byte[][] List { get; private set; } = [];
        public bool SupportsBlobs => false;
        public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec) => List = inclusionListTransactions;
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, BlockHeader targetBlock, ulong gasLimit,
            PayloadAttributes payloadAttributes = null, bool filterSource = false) => [];
    }

    private sealed class CountingEcdsa(IEthereumEcdsa inner) : IEthereumEcdsa
    {
        public int Recoveries;
        public ulong ChainId => inner.ChainId;

        public Address RecoverAddress(Signature signature, in ValueHash256 message)
        {
            Recoveries++;
            return inner.RecoverAddress(signature, in message);
        }

        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => inner.Sign(privateKey, in message);
        public PublicKey RecoverPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverPublicKey(signature, in message);
        public CompressedPublicKey RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) => inner.RecoverCompressedPublicKey(signature, in message);
    }

    /// <summary>Retains a verified proof as a mempool wrapper would and returns the commitment naming it.</summary>
    private static RecursiveStarkV1 Hold(LeanProofStore store, ILeanProofVerifier verifier, byte[] proof, IReadOnlyList<FrameDependency> deps)
    {
        store.AddVerified(deps, null, proof);
        ProofVerdictCache.VerifyRecursive(verifier, Eip8288Verifier.Pinned, Eip8288Dependencies.ComputeDepsHash(deps), proof, deps);
        return new RecursiveStarkV1 { StarkProofHash = Keccak.Compute(proof), DepsHash = new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)) };
    }
}
