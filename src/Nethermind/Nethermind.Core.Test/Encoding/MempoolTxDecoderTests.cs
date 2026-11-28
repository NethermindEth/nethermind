// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public partial class MempoolTxDecoderTests
{
    private const int BlobCountLimit = 128;
    private const int BlobCellProofsCountLimit = BlobCountLimit * Ckzg.CellsPerExtBlob;

    private static readonly MempoolTxDecoder _decoder = MempoolTxDecoder.Instance;

    [OneTimeSetUp]
    public static Task OneTimeSetUp() => KzgPolynomialCommitments.InitializeAsync();

    [TestCaseSource(nameof(RoundtripCases))]
    public void Roundtrip_preserves_transaction_sidecar_and_canonical_hash(MempoolTransaction mempoolTx, RlpBehaviors rlpBehaviors)
    {
        byte[] encoded = _decoder.Encode(mempoolTx, rlpBehaviors);
        RlpReader reader = new(encoded);

        MempoolTransaction decoded = _decoder.Decode(ref reader, rlpBehaviors)!;

        byte[] canonical = TxDecoder.Instance.Encode(mempoolTx.Transaction, rlpBehaviors);
        RlpReader canonicalReader = new(canonical);
        Transaction canonicalDecoded = TxDecoder.Instance.Decode(ref canonicalReader, rlpBehaviors)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Has.Length.EqualTo(_decoder.GetLength(mempoolTx, rlpBehaviors)));
            Assert.That(decoded, Is.EqualTo(mempoolTx with { Transaction = canonicalDecoded }).UsingMempoolTransactionComparer());
            Assert.That(decoded.Hash, Is.EqualTo(mempoolTx.Transaction.CalculateHash()));
            if (mempoolTx.NetworkWrapper is null)
            {
                Assert.That(encoded, Is.EqualTo(canonical));
            }
        }
    }

    [TestCaseSource(nameof(ShardBlobTxTests))]
    public void NetworkWrapper_is_decoded_correctly(string rlp, Hash256 signedHash, RlpBehaviors rlpBehaviors)
    {
        byte[] incomingTxRlp = Bytes.FromHexString(rlp);
        RlpReader reader = new(incomingTxRlp);

        MempoolTransaction decoded = _decoder.Decode(ref reader, rlpBehaviors)!;

        RlpReader canonicalReader = new(TxDecoder.Instance.Encode(decoded.Transaction, rlpBehaviors));
        ShardBlobNetworkWrapper wrapper = decoded.NetworkWrapper!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Hash, Is.EqualTo(signedHash));
            Assert.That(TxDecoder.Instance.Decode(ref canonicalReader, rlpBehaviors)!.Hash, Is.EqualTo(signedHash));
            Assert.That(IBlobProofsManager.For(wrapper.Version).ValidateProofs(wrapper));
            Assert.That(_decoder.Encode(decoded, rlpBehaviors), Is.EqualTo(incomingTxRlp));
        }
    }

    [TestCaseSource(nameof(BlobCarryingTransactionsWithoutSidecar))]
    public void Decode_rejects_blob_carrying_transaction_without_sidecar(byte[] encoded) =>
        Assert.That(() =>
        {
            RlpReader reader = new(encoded);
            _decoder.Decode(ref reader, RlpBehaviors.SkipTypedWrapping);
        }, Throws.InstanceOf<RlpReaderException>());

    [TestCaseSource(nameof(SidecarMismatchCases))]
    public void Encode_rejects_sidecar_not_matching_transaction_type(MempoolTransaction mempoolTx) =>
        Assert.That(
            () => _decoder.Encode(mempoolTx),
            Throws.TypeOf<RlpReaderException>().With.Message.Contains(nameof(ShardBlobNetworkWrapper)));

    [TestCaseSource(nameof(OverLimitWrapperCases))]
    public void Decode_rejects_more_than_blob_count_limit_for_decoding(MempoolTransaction mempoolTx)
    {
        byte[] encoded = _decoder.Encode(mempoolTx);

        Assert.That(() =>
        {
            RlpReader reader = new(encoded);
            _decoder.Decode(ref reader);
        }, Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void Decode_allows_v1_wrapper_proofs_up_to_cell_proof_limit()
    {
        MempoolTransaction mempoolTx = BuildMempoolTransactionWithWrapperCounts(
            BlobCountLimit,
            BlobCountLimit,
            BlobCellProofsCountLimit,
            ProofVersion.V1);
        byte[] encoded = _decoder.Encode(mempoolTx);

        RlpReader reader = new(encoded);
        MempoolTransaction decoded = _decoder.Decode(ref reader)!;

        Assert.That(decoded.NetworkWrapper!.ProofCount, Is.EqualTo(BlobCellProofsCountLimit));
    }

    [Test]
    public void Proofs_encode_as_a_list_of_proof_strings_and_round_trip([Values] ProofVersion version, [Values] bool storage)
    {
        MempoolTransaction mempoolTx = BuildBlobTransaction(version is ProofVersion.V1 ? Osaka.Instance : Cancun.Instance);
        ShardBlobNetworkWrapper wrapper = mempoolTx.NetworkWrapper!;
        RlpBehaviors behaviors = storage ? RlpBehaviors.Storage : RlpBehaviors.None;

        byte[] encoded = _decoder.Encode(mempoolTx, behaviors);
        RlpReader reader = new(encoded);
        MempoolTransaction decoded = _decoder.Decode(ref reader, behaviors)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.AsSpan().IndexOf(RlpTestHelper.EncodeSequence([.. wrapper.SplitProofs().Select(static proof => Rlp.Encode(proof))])), Is.GreaterThan(0));
            Assert.That(decoded.NetworkWrapper!.Proofs, Is.EqualTo(wrapper.Proofs));
            Assert.That(_decoder.Encode(decoded, behaviors), Is.EqualTo(encoded));
            Assert.That(_decoder.GetLength(mempoolTx, behaviors), Is.EqualTo(encoded.Length));
        }
    }

    [Test]
    public void Decode_rejects_a_proof_that_is_not_48_bytes([Values(0, 47, 49)] int proofLength)
    {
        MempoolTransaction mempoolTx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1, spec: Cancun.Instance)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved()
            .MempoolTestObject;
        ShardBlobNetworkWrapper wrapper = mempoolTx.NetworkWrapper!;
        byte[] payload = TxDecoder.Instance.Encode(mempoolTx.Transaction, RlpBehaviors.SkipTypedWrapping);
        byte[] tampered = Bytes.Concat((byte)TxType.Blob, RlpTestHelper.EncodeSequence(
            payload[1..],
            RlpTestHelper.EncodeSequence([.. wrapper.Blobs.Select(static blob => Rlp.Encode(blob))]),
            RlpTestHelper.EncodeSequence([.. wrapper.Commitments.Select(static commitment => Rlp.Encode(commitment))]),
            RlpTestHelper.EncodeSequence(Rlp.Encode(new byte[proofLength]))));

        Assert.That(() =>
        {
            RlpReader reader = new(tampered);
            _decoder.Decode(ref reader, RlpBehaviors.SkipTypedWrapping);
        }, Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void Mempool_form_encodes_elided_blob_payload_as_empty_rlp_list([Values(ProofVersion.V0, ProofVersion.V1)] ProofVersion version)
    {
        MempoolTransaction mempoolTx = BuildMempoolTransactionWithWrapperCounts(0, 1, 1, version);
        byte[] encoded = _decoder.Encode(mempoolTx);

        RlpReader outer = new(encoded);
        ReadOnlySpan<byte> envelope = outer.ReadBytes();
        Assert.That(envelope[0], Is.EqualTo((byte)TxType.Blob));
        RlpReader wireReader = new RlpReader(envelope[1..]).EnterSequence(out _);
        wireReader.SkipItem();
        if (version is ProofVersion.V1)
        {
            Assert.That(wireReader.ReadRaw(1)[0], Is.EqualTo((byte)ProofVersion.V1));
        }

        Assert.That(wireReader.ReadRaw(1)[0], Is.EqualTo(Rlp.EmptyListByte));

        RlpReader reader = new(encoded);
        Assert.That(_decoder.Decode(ref reader)!.NetworkWrapper!.Blobs, Is.Empty);
    }

    private static IEnumerable<TestCaseData> RoundtripCases()
    {
        (string Name, MempoolTransaction Tx)[] transactions =
        [
            ("Legacy", Build.A.Transaction.WithChainId(TestBlockchainIds.ChainId).SignedAndResolved().TestObject),
            ("EIP-1559", Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(TestBlockchainIds.ChainId).WithMaxFeePerGas(2).SignedAndResolved().TestObject),
            ("Blob with V0 sidecar", BuildBlobTransaction(Cancun.Instance)),
            ("Blob with V1 sidecar", BuildBlobTransaction(Osaka.Instance)),
            ("Frame without sidecar", FrameTxDecoderTests.CreateFrameTx()),
            ("Frame with V0 sidecar", BuildBlobCarryingFrameTransaction(Cancun.Instance)),
            ("Frame with V1 sidecar", BuildBlobCarryingFrameTransaction(Osaka.Instance)),
        ];

        foreach ((string name, MempoolTransaction tx) in transactions)
        {
            foreach (RlpBehaviors rlpBehaviors in new[] { RlpBehaviors.None, RlpBehaviors.SkipTypedWrapping })
            {
                yield return new TestCaseData(tx, rlpBehaviors).SetName($"Roundtrip {name} ({rlpBehaviors})");
            }
        }
    }

    private static IEnumerable<TestCaseData> BlobCarryingTransactionsWithoutSidecar()
    {
        (string Name, MempoolTransaction Tx)[] transactions =
        [
            ("Blob", BuildBlobTransaction(Cancun.Instance)),
            ("Frame", BuildBlobCarryingFrameTransaction(Cancun.Instance)),
        ];

        foreach ((string name, MempoolTransaction tx) in transactions)
        {
            byte[] canonical = TxDecoder.Instance.Encode(tx.Transaction, RlpBehaviors.SkipTypedWrapping);
            yield return new TestCaseData(canonical).SetName($"{name} in canonical form");
            yield return new TestCaseData((byte[])[canonical[0], .. RlpTestHelper.EncodeSequence(canonical[1..])])
                .SetName($"{name} in wrapper holding only the payload");
        }
    }

    private static IEnumerable<TestCaseData> SidecarMismatchCases()
    {
        MempoolTransaction blobTx = BuildBlobTransaction(Cancun.Instance);
        yield return new TestCaseData(blobTx with { NetworkWrapper = null }).SetName("Blob transaction without sidecar");
        yield return new TestCaseData(BuildBlobCarryingFrameTransaction(Cancun.Instance) with { NetworkWrapper = null })
            .SetName("Blob-carrying frame transaction without sidecar");
        yield return new TestCaseData(new MempoolTransaction(Build.A.Transaction.SignedAndResolved().TestObject, blobTx.NetworkWrapper))
            .SetName("Legacy transaction with sidecar");
    }

    private static IEnumerable<TestCaseData> OverLimitWrapperCases()
    {
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(BlobCountLimit + 1, 1, 1))
        {
            TestName = "Decode rejects more than 128 wrapper blobs"
        };
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(1, BlobCountLimit + 1, 1))
        {
            TestName = "Decode rejects more than 128 wrapper commitments"
        };
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(1, 1, BlobCountLimit + 1))
        {
            TestName = "Decode rejects more than 128 V0 wrapper proofs"
        };
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(1, 1, BlobCellProofsCountLimit + 1, ProofVersion.V1))
        {
            TestName = "Decode rejects more than 16384 V1 wrapper proofs"
        };
    }

    private static MempoolTransaction BuildBlobTransaction(IReleaseSpec spec) =>
        Build.A.Transaction
            .WithShardBlobTxTypeAndFields(blobCount: 2, spec: spec)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved()
            .MempoolTestObject;

    private static MempoolTransaction BuildBlobCarryingFrameTransaction(IReleaseSpec spec)
    {
        MempoolTransaction blobTx = BuildBlobTransaction(spec);
        Transaction frameTx = FrameTxDecoderTests.CreateFrameTx();
        frameTx.BlobVersionedHashes = blobTx.Transaction.BlobVersionedHashes;
        return new MempoolTransaction(frameTx, blobTx.NetworkWrapper);
    }

    private static MempoolTransaction BuildMempoolTransactionWithWrapperCounts(
        int blobsCount,
        int commitmentsCount,
        int proofsCount,
        ProofVersion version = ProofVersion.V0) =>
        new(
            Build.A.Transaction
                .WithShardBlobTxTypeAndFields(1, false)
                .WithChainId(TestBlockchainIds.ChainId)
                .SignedAndResolved()
                .TestObject,
            new ShardBlobNetworkWrapper(CreateEmptyByteArrays(blobsCount), CreateEmptyByteArrays(commitmentsCount), new byte[proofsCount * Ckzg.BytesPerProof], version));

    private static byte[][] CreateEmptyByteArrays(int count) => [.. Enumerable.Repeat(Array.Empty<byte>(), count)];
}
