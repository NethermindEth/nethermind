// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CkzgLib;
using MathNet.Numerics.Random;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public partial class ShardBlobTxDecoderTests
{
    private const int BlobCountLimit = 128;
    private const int BlobCellProofsCountLimit = BlobCountLimit * Ckzg.CellsPerExtBlob;

    private readonly TxDecoder _txDecoder = TxDecoder.Instance;

    [SetUp]
    public static Task SetUp() => KzgPolynomialCommitments.InitializeAsync();

    public static IEnumerable<(Transaction, string)> TestCaseSource() =>
        TxDecoderTests.TestCaseSource().Select(static tos => (Build.A.Transaction.From(tos.Item1)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithShardBlobTxTypeAndFields(2, false)
            .SignedAndResolved()
            .TestObject, tos.Item2));

    [TestCaseSource(nameof(TestCaseSource))]
    public void Roundtrip_ExecutionPayloadForm_for_shard_blobs((Transaction Tx, string Description) testCase)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, testCase.Tx);
        RlpReader ctx = new(bytes);
        Transaction? decoded = _txDecoder.Decode(ref ctx);
        decoded!.SenderAddress =
            new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
        decoded.Hash = decoded.CalculateHash();
        Assert.That(decoded, Is.EqualTo(testCase.Tx).UsingTransactionComparer());
    }

    [Test]
    public void TestDecodeTamperedBlob()
    {
        byte[] bytes = Bytes.FromHexString(
            "b8aa03f8a7018001808252089400000000000000000000000000000000000000000180c001f841a00100000000000000000000000000000000000000000000000000000000000000a0010000000000000000000000000000000000000000000000000000000000000080a00fb9ad625df88e2fea9e088b69a31497f0d9b767067db8c03fd2453d7092e7bfa0086f2930db968d992d0fb06ddc903ca5522ba38bedc0530eb28b61082897efa1");

        Action tryDecode = () =>
        {
            RlpReader ctx = new(bytes);
            _txDecoder.Decode(ref ctx);
        };
        Assert.That(tryDecode, Throws.TypeOf<RlpException>());
    }

    [TestCaseSource(nameof(TestCaseSource))]
    public void Roundtrip_RlpReader_ExecutionPayloadForm_for_shard_blobs((Transaction Tx, string Description) testCase)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, testCase.Tx);

        Span<byte> spanIncomingTxRlp = bytes.AsSpan();
        RlpReader decoderContext = new(spanIncomingTxRlp);
        Transaction? decoded = _txDecoder.Decode(ref decoderContext);
        decoded!.SenderAddress =
            new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
        decoded.Hash = decoded.CalculateHash();
        Assert.That(decoded, Is.EqualTo(testCase.Tx).UsingTransactionComparer());
    }

    private static IEnumerable<Transaction> TamperedTestCaseSource()
    {
        yield return Build.A.Transaction
            .WithShardBlobTxTypeAndFields(2, false)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved()
            .TestObject;
        yield return Build.A.Transaction
            .WithShardBlobTxTypeAndFields(2, false)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithNonce(0)
            .SignedAndResolved()
            .TestObject;
    }

    [TestCaseSource(nameof(TamperedTestCaseSource))]
    public void Tampered_Roundtrip_ExecutionPayloadForm_for_shard_blobs(Transaction tx)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(tx, RlpBehaviors.None)];
        RlpWriter writer = new(bytes);
        _txDecoder.Encode(ref writer, tx);
        // Tamper with sequence length
        {
            int itemsLength = 0;
            foreach (byte[]? array in tx.BlobVersionedHashes!)
            {
                itemsLength += Rlp.LengthOf(array);
            }

            // Position where it starts encoding `BlobVersionedHashes`
            RlpWriter tamperingWriter = new(bytes.AsSpan(37));
            // Accepts `itemsLength - 10` all the way to `itemsLength - 1`
            tamperingWriter.StartSequence(itemsLength - 1);
        }

        // Decoding should fail
        Action tryDecode = () =>
        {
            RlpReader ctx = new(bytes);
            _txDecoder.Decode(ref ctx);
        };
        Assert.That(tryDecode, Throws.TypeOf<RlpException>());
    }

    [TestCaseSource(nameof(OverLimitCollectionDecodeCases))]
    public void Decode_rejects_more_than_blob_count_limit_for_decoding(Transaction tx, RlpBehaviors rlpBehaviors)
    {
        Rlp encoded = _txDecoder.Encode(tx, rlpBehaviors);

        void DecodeByRlpReader()
        {
            RlpReader decoderContext = new(encoded.Bytes);
            _txDecoder.Decode(ref decoderContext, rlpBehaviors);
        }

        Assert.That(DecodeByRlpReader, Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Decode_allows_v1_wrapper_proofs_up_to_cell_proof_limit()
    {
        Transaction tx = BuildMempoolTransactionWithWrapperCounts(
            BlobCountLimit,
            BlobCountLimit,
            BlobCellProofsCountLimit,
            ProofVersion.V1);
        Rlp encoded = _txDecoder.Encode(tx, RlpBehaviors.InMempoolForm);

        RlpReader decoderContext = new(encoded.Bytes);
        Transaction? decoded = _txDecoder.Decode(ref decoderContext, RlpBehaviors.InMempoolForm);

        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)decoded!.NetworkWrapper!;
        Assert.That(wrapper.Proofs, Has.Length.EqualTo(BlobCellProofsCountLimit));
    }

    // Other clients validate each blob length
    [Test]
    public void Rejects_blob_tx_with_invalid_versioned_hash_length(
        [Values(0, 1, 31, 33)] int invalidLength
    )
    {
        Transaction tx = Build.A.Transaction
            .WithChainId(TestBlockchainIds.ChainId)
            .WithShardBlobTxTypeAndFields(4, false)
            .WithBlobVersionedHashes([
                Random.Shared.NextBytes(32),
                Random.Shared.NextBytes(32),
                Random.Shared.NextBytes(invalidLength),
                Random.Shared.NextBytes(32)
            ])
            .SignedAndResolved()
            .TestObject;

        byte[] rlp = _txDecoder.Encode(tx).Bytes;

        Assert.That(() =>
        {
            RlpReader ctx = new(rlp);
            _txDecoder.Decode(ref ctx);
        }, Throws.InstanceOf<RlpException>());
    }

    [TestCaseSource(nameof(ShardBlobTxTests))]
    public void NetworkWrapper_is_decoded_correctly(string rlp, Hash256 signedHash, RlpBehaviors rlpBehaviors)
    {
        byte[] spanIncomingTxRlp = Bytes.FromHexString(rlp);
        RlpReader ctx = new(spanIncomingTxRlp.AsSpan());
        RlpReader decoderContext = new(spanIncomingTxRlp.AsSpan());

        Transaction? decoded = _txDecoder.Decode(ref ctx, rlpBehaviors);
        Transaction? decodedByRlpReader = _txDecoder.Decode(ref decoderContext, rlpBehaviors);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded!.Hash, Is.EqualTo(signedHash));
            Assert.That(decodedByRlpReader!.Hash, Is.EqualTo(signedHash));
        }

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            Rlp epEncoded = _txDecoder.Encode(decoded!, rlpBehaviors ^ RlpBehaviors.InMempoolForm);
            RlpReader epCtx = new(epEncoded.Bytes);
            Transaction? epDecoded = _txDecoder.Decode(ref epCtx, rlpBehaviors ^ RlpBehaviors.InMempoolForm);
            Assert.That(epDecoded!.Hash, Is.EqualTo(signedHash));
        }

        if (decoded is { NetworkWrapper: ShardBlobNetworkWrapper wrapper })
        {
            Assert.That(IBlobProofsManager.For(wrapper.Version).ValidateProofs(wrapper));
        }

        Rlp encoded = _txDecoder.Encode(decoded!, rlpBehaviors);
        Rlp encodedWithDecodedByRlpReader =
            _txDecoder.Encode(decodedByRlpReader!, rlpBehaviors);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.Bytes, Is.EqualTo(spanIncomingTxRlp));
            Assert.That(encodedWithDecodedByRlpReader.Bytes, Is.EqualTo(spanIncomingTxRlp));
        }
    }

    private static IEnumerable<TestCaseData> OverLimitCollectionDecodeCases()
    {
        static Transaction BuildTransactionWithBlobVersionedHashCount(int count) =>
            Build.A.Transaction
                .WithShardBlobTxTypeAndFields(1, false)
                .WithChainId(TestBlockchainIds.ChainId)
                .WithBlobVersionedHashes(count)
                .SignedAndResolved()
                .TestObject;

        yield return new TestCaseData(BuildTransactionWithBlobVersionedHashCount(BlobCountLimit + 1), RlpBehaviors.None)
        {
            TestName = "Decode rejects more than 128 blob versioned hashes"
        };
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(BlobCountLimit + 1, 1, 1), RlpBehaviors.InMempoolForm)
        {
            TestName = "Decode rejects more than 128 wrapper blobs"
        };
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(1, BlobCountLimit + 1, 1), RlpBehaviors.InMempoolForm)
        {
            TestName = "Decode rejects more than 128 wrapper commitments"
        };
        yield return new TestCaseData(BuildMempoolTransactionWithWrapperCounts(1, 1, BlobCountLimit + 1), RlpBehaviors.InMempoolForm)
        {
            TestName = "Decode rejects more than 128 V0 wrapper proofs"
        };
        yield return new TestCaseData(
            BuildMempoolTransactionWithWrapperCounts(1, 1, BlobCellProofsCountLimit + 1, ProofVersion.V1),
            RlpBehaviors.InMempoolForm)
        {
            TestName = "Decode rejects more than 16384 V1 wrapper proofs"
        };
    }

    private static Transaction BuildMempoolTransactionWithWrapperCounts(
        int blobsCount,
        int commitmentsCount,
        int proofsCount,
        ProofVersion version = ProofVersion.V0)
    {
        byte[][] blobs = CreateEmptyByteArrays(blobsCount);
        byte[][] commitments = CreateEmptyByteArrays(commitmentsCount);
        byte[][] proofs = CreateEmptyByteArrays(proofsCount);
        return Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1, false)
            .WithChainId(TestBlockchainIds.ChainId)
            .With(tx => tx.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs, version))
            .SignedAndResolved()
            .TestObject;
    }

    private static byte[][] CreateEmptyByteArrays(int count)
    {
        byte[][] arrays = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            arrays[i] = [];
        }

        return arrays;
    }
}
