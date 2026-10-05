// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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

using NSubstitute;

namespace Nethermind.Consensus.Test;

public class InclusionListTxSourceTests
{
    private static InclusionListTxSource CreateSource() => new(
        new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
        new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)),
        LimboLogs.Instance,
        Substitute.For<Nethermind.Core.Crypto.ILeanProofVerifier>());

    private static PayloadAttributes Attributes(byte[][] inclusionList) => new() { InclusionListTransactions = inclusionList };

    private static IEnumerable<Transaction> GetTransactions(
        InclusionListTxSource source,
        PayloadAttributes payloadAttributes = null) =>
        source.GetTransactions(
            Build.A.BlockHeader.WithNumber(0).TestObject,
            Build.A.BlockHeader.WithNumber(1).TestObject,
            30_000_000UL,
            payloadAttributes);

    [Test]
    public void Legacy_source_preserves_list_and_proof_obligations_without_claiming_verified_coverage()
    {
        LegacyInclusionListTxSource legacy = new();
        IInclusionListTxSource source = legacy;
        Transaction transaction = Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        byte[][] list = [Encode(transaction)];
        RecursiveStark proof = new([1], Keccak.Zero);
        byte[] dependencies = [1];
        PayloadAttributes attributes = new()
        {
            InclusionListTransactions = list,
            InclusionListRecursiveStark = proof,
            InclusionListProvenDependencies = dependencies
        };

        source.Set(list, Bogota.Instance, proof, dependencies);
        BlockToProduce block = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(block, attributes);

        Assert.That(legacy.List, Is.SameAs(list));
        Assert.That(block.InclusionListTransactions.Single().Nonce, Is.EqualTo(transaction.Nonce));
        Assert.That(block.InclusionListRecursiveStark, Is.SameAs(proof));
        Assert.That(block.InclusionListProvenDependencies, Is.SameAs(dependencies));
        Assert.That(block.InclusionListProofInput, Is.Null);
    }

    [Test]
    public void Payload_improvements_reuse_one_verified_snapshot()
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        FakeLeanProofVerifier verifier = new(true);
        LeanProofStore store = new();
        InclusionListTxSource source = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new TestSingleReleaseSpecProvider(spec), LimboLogs.Instance, verifier, store);
        Transaction transaction = FrameTx(0, null!);
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = Eip8288Constants.LeanSphincsScheme;
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)];
        transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
        byte[][] list = [Encode(transaction)];
        List<FrameDependency> deps = Eip8288Dependencies.ForTransaction(transaction);
        byte[] proven = Eip8288Dependencies.Serialize(deps);
        RecursiveStark proof = new([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(deps)));
        PayloadAttributes attributes = new() { InclusionListTransactions = list, InclusionListRecursiveStark = proof, InclusionListProvenDependencies = proven };
        source.Set(list, spec, proof, proven);
        source.Set(list, spec, proof, proven);
        Assert.That(verifier.VerificationCalls, Is.Zero);
        Assert.That(GetTransactions(source, attributes).Single().Nonce, Is.Zero);
        BlockToProduce firstBuild = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(firstBuild, attributes);
        BlockToProduce nextBuild = new(Build.A.BlockHeader.WithNumber(1).TestObject);
        source.ApplyInclusionList(nextBuild, attributes);
        Assert.That(nextBuild.InclusionListTransactions, Is.SameAs(firstBuild.InclusionListTransactions));
        Assert.That(nextBuild.InclusionListProofInput, Is.SameAs(firstBuild.InclusionListProofInput));
        source.Set(list, spec, proof, proven);
        // The registered build owns its snapshot even if the caller mutates the encoded list.
        list[0][0] = 0xff;
        for (int improvement = 0; improvement < 12; improvement++)
            Assert.That(GetTransactions(source, attributes).Single().Nonce, Is.Zero);
        Assert.That(verifier.VerificationCalls, Is.EqualTo(1));
        Assert.That(store.Covers(transaction), Is.True);
        proof.StarkProof[0] = 2;
        Assert.That(GetTransactions(source, attributes), Is.Empty);
    }

    [Test]
    public void Malformed_dependency_frame_is_rejected_before_proof_verification()
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        FakeLeanProofVerifier verifier = new(true);
        InclusionListTxSource source = new(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId),
            new TestSingleReleaseSpecProvider(spec), LimboLogs.Instance, verifier, new LeanProofStore());
        Transaction transaction = FrameTx(0, null!);
        transaction.Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, 0, UInt256.Zero, new byte[96])];
        byte[][] list = [Encode(transaction)];
        RecursiveStark proof = new([1], Keccak.Zero);
        PayloadAttributes attributes = new() { InclusionListTransactions = list, InclusionListRecursiveStark = proof };
        source.Set(list, spec, proof);
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
        InclusionListTxSource source = new(ecdsa, new CustomSpecProvider(((ForkActivation)0, Bogota.Instance)), LimboLogs.Instance, Substitute.For<Nethermind.Core.Crypto.ILeanProofVerifier>());
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
}
