// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public class ShardBlobTxDecoderTests
{
    private const int BlobCountLimit = 128;

    private readonly TxDecoder _txDecoder = TxDecoder.Instance;

    [SetUp]
    public static Task SetUp() => KzgPolynomialCommitments.InitializeAsync();

    [Test]
    public void V1_sparse_wrapper_without_cells_is_not_proof_valid()
    {
        ShardBlobNetworkWrapper wrapper = CreateV1SparseWrapperWithoutCells(blobCount: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BlobCellsHelper.ValidateCells(wrapper), Is.False);
            Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateProofs(wrapper), Is.False);
        }
    }

    [Test]
    public void V1_length_validation_rejects_null_commitments_and_short_proofs()
    {
        ShardBlobNetworkWrapper wrapper = CreateV1SparseWrapperWithoutCells(blobCount: 1);
        wrapper.Commitments[0] = null!;

        Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateLengths(wrapper), Is.False);

        wrapper = CreateV1SparseWrapperWithoutCells(blobCount: 1);
        wrapper = wrapper with { Proofs = wrapper.Proofs[..^1] };

        Assert.That(IBlobProofsManager.For(ProofVersion.V1).ValidateLengths(wrapper), Is.False);
    }

    [Test]
    public void Select_flattened_cells_rejects_non_subset_mask()
    {
        byte[][] selected = BlobCellsHelper.SelectFlattenedCells(
            [new byte[Ckzg.BytesPerCell]],
            new BlobCellMask(UInt128.One),
            new BlobCellMask(UInt128.One << 1),
            blobCount: 1);

        Assert.That(selected, Is.Empty);
    }

    [Test]
    public void Select_proofs_rejects_invalid_blob_index()
    {
        ShardBlobNetworkWrapper wrapper = CreateV1SparseWrapperWithoutCells(blobCount: 1);

        Assert.That(BlobCellsHelper.SelectProofs(wrapper, blobIndex: 1, BlobCellMask.Full), Is.Empty);
    }

    public static IEnumerable<(Transaction, string)> TestCaseSource() =>
        TxDecoderTests.TestCaseSource().Select(static tos => (Build.A.Transaction.From(tos.Item1)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithShardBlobTxTypeAndFields(2, false)
            .SignedAndResolved()
            .TestObject, tos.Item2));

    private static ShardBlobNetworkWrapper CreateV1SparseWrapperWithoutCells(int blobCount)
    {
        byte[][] commitments = new byte[blobCount][];
        byte[] proofs = new byte[blobCount * Ckzg.CellsPerExtBlob * Ckzg.BytesPerProof];
        for (int i = 0; i < blobCount; i++)
        {
            commitments[i] = new byte[Ckzg.BytesPerCommitment];
        }

        return new ShardBlobNetworkWrapper([], commitments, proofs, ProofVersion.V1);
    }

    [TestCaseSource(nameof(TestCaseSource))]
    public void Roundtrip_ExecutionPayloadForm_for_shard_blobs((Transaction Tx, string Description) testCase)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
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
        Assert.That(tryDecode, Throws.TypeOf<RlpReaderException>());
    }

    [TestCaseSource(nameof(TestCaseSource))]
    public void Roundtrip_RlpReader_ExecutionPayloadForm_for_shard_blobs((Transaction Tx, string Description) testCase)
    {
        byte[] bytes = new byte[_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None)];
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
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
        RlpWriter writer = RlpWriter.ContentWriter(bytes);
        _txDecoder.Encode(ref writer, tx);
        // Tamper with sequence length
        {
            int itemsLength = 0;
            foreach (byte[]? array in tx.BlobVersionedHashes!)
            {
                itemsLength += Rlp.LengthOf(array);
            }

            // Position where it starts encoding `BlobVersionedHashes`
            RlpWriter tamperingWriter = RlpWriter.ContentWriter(bytes.AsSpan(37));
            // Accepts `itemsLength - 10` all the way to `itemsLength - 1`
            tamperingWriter.StartSequence(itemsLength - 1);
        }

        // Decoding should fail
        Action tryDecode = () =>
        {
            RlpReader ctx = new(bytes);
            _txDecoder.Decode(ref ctx);
        };
        Assert.That(tryDecode, Throws.TypeOf<RlpReaderException>());
    }

    [Test]
    public void Decode_rejects_more_than_blob_count_limit_for_decoding()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(1, false)
            .WithChainId(TestBlockchainIds.ChainId)
            .WithBlobVersionedHashes(BlobCountLimit + 1)
            .SignedAndResolved()
            .TestObject;
        byte[] encoded = _txDecoder.Encode(tx);

        void DecodeByRlpReader()
        {
            RlpReader decoderContext = new(encoded);
            _txDecoder.Decode(ref decoderContext);
        }

        Assert.That(DecodeByRlpReader, Throws.InstanceOf<RlpReaderException>());
    }

    [TestCase(nameof(Transaction.MaxFeePerBlobGas))]
    [TestCase(nameof(Transaction.BlobVersionedHashes))]
    public void Encode_rejects_blob_transaction_missing_required_payload(string missingField)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(blobCount: 1, isMempoolTx: true)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved()
            .TestObject;
        switch (missingField)
        {
            case nameof(Transaction.MaxFeePerBlobGas):
                tx.MaxFeePerBlobGas = null;
                break;
            case nameof(Transaction.BlobVersionedHashes):
                tx.BlobVersionedHashes = null;
                break;
        }

        Assert.That(
            () => _txDecoder.Encode(tx),
            Throws.TypeOf<RlpReaderException>().With.Message.Contains(missingField));
    }

    [Test]
    public void Encode_rejects_blob_transaction_with_null_versioned_hash()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(blobCount: 1, isMempoolTx: true)
            .WithChainId(TestBlockchainIds.ChainId)
            .SignedAndResolved()
            .TestObject;
        byte[]?[] blobVersionedHashes = tx.BlobVersionedHashes
            ?? throw new InvalidOperationException($"{nameof(Transaction.BlobVersionedHashes)} was not initialized.");
        blobVersionedHashes[0] = null;

        Assert.That(
            () => _txDecoder.Encode(tx),
            Throws.TypeOf<RlpReaderException>().With.Message.Contains(nameof(Transaction.BlobVersionedHashes)));
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

        byte[] rlp = _txDecoder.Encode(tx);

        Assert.That(() =>
        {
            RlpReader ctx = new(rlp);
            _txDecoder.Decode(ref ctx);
        }, Throws.InstanceOf<RlpReaderException>());
    }

    [Test]
    public void Execution_payload_form_is_decoded_correctly()
    {
        byte[] incomingTxRlp = Bytes.FromHexString(
            "0x03f901012a018207d0843b9aca008252089409f2099b5a5153dd6f62bc888795d652472969a28203e880f873d6947e5f4552091a69125d5dfcb7b8c2659029395bdfc0d6942b5ad5c4795c026514f8317c7a215e218dccd6cfc0d6946813eb9362372eef6200f3b1dbc3f819671cba69c0d6941eff47bc3a10a45d4b230b5d10e37751fe6aa718c0d694e1ab8145f7e55dc933d51a18c793f901a3a0b276c001e1a0010657f37554c781402a22917dee2f75def7ab966d7b770905398eba3c44401480a08f62dba675e8383932d1bed59ded809fe8226aebb0a802e40f7eaed3904a9a06a01962e718af18d089775ed2cb162c53403c88d63091cdcdb9c7504e2b12426716");
        RlpReader ctx = new(incomingTxRlp);

        Transaction decoded = _txDecoder.Decode(ref ctx, RlpBehaviors.SkipTypedWrapping)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Hash, Is.EqualTo(new Hash256("0x8dc1d45834ec7d1480feba0d28699198fb88a890603ba603dd9921b0a8571229")));
            Assert.That(_txDecoder.Encode(decoded, RlpBehaviors.SkipTypedWrapping), Is.EqualTo(incomingTxRlp));
        }
    }
}
