// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Network.Enr;
using Nethermind.Runner.Ethereum.WebTransport;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using TxBuilder = Nethermind.Core.Test.Builders.Build;

namespace Nethermind.Runner.Test.Ethereum.WebTransport;

public class BrowserProtocolTests
{
    [TestCase(BrowserProtocol.TxMode, "webtx/1")]
    [TestCase(BrowserProtocol.DiscoveryMode, "webdisc/1")]
    public void Handshake_signs_the_geth_challenge_with_the_enr_key(byte mode, string capability)
    {
        using BrowserProtocol protocol = CreateProtocol(out _);
        byte[] nonce = [.. Enumerable.Range(0, BrowserProtocol.NonceLength).Select(static i => (byte)i)];
        byte[] handshake = protocol.CreateHandshake(mode, nonce);
        RlpReader reader = new(handshake);
        RlpReader fields = reader.EnterSequence(out int length);
        byte[] proof = fields.DecodeByteArray(size: Signature.Size);
        RlpReader capabilities = fields.EnterSequence(out int capabilitiesLength);
        string negotiated = capabilities.DecodeString();
        fields.ExitSequence(in capabilities, capabilitiesLength);
        reader.ExitSequence(in fields, length);
        Assert.That(reader.HasNext, Is.False);

        // Keccak-256 of the ASCII label followed by the bytes 00 through 1f.
        ValueHash256 expectedDigest = new("9bf4877f40b1525d32cca56e2ff5ea6d007b12aebe6a542bfa0103bdc70b483e");
        using Signature signature = new(proof.AsSpan(0, 64), proof[64]);
        PublicKey? recovered = new Ecdsa().RecoverPublicKey(signature, in expectedDigest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(negotiated, Is.EqualTo(capability));
            Assert.That(proof[64], Is.InRange(0, 1));
            Assert.That(recovered, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));
        }
    }

    [Test]
    public void Submit_preserves_batch_order_and_encodes_pool_outcomes()
    {
        using BrowserProtocol protocol = CreateProtocol(out ITxPool pool);
        AcceptTxResult[] outcomes =
        [
            AcceptTxResult.Accepted, AcceptTxResult.AlreadyKnown, AcceptTxResult.OldNonce,
            AcceptTxResult.ReplacementNotAllowed, AcceptTxResult.FeeTooLow, AcceptTxResult.InsufficientFunds,
            AcceptTxResult.GasLimitExceeded.WithMessage("detail"), AcceptTxResult.Syncing, AcceptTxResult.FailedToResolveSender
        ];
        List<Hash256?> submitted = [];
        pool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None).Returns(call =>
        {
            submitted.Add(call.Arg<Transaction>().Hash);
            return outcomes[submitted.Count - 1];
        });
        Transaction[] transactions =
        [
            .. Enumerable.Range(0, outcomes.Length)
                .Select(static i => SignedTransaction(i % 2 == 0 ? TxType.Legacy : TxType.EIP1559, i))
        ];
        byte[] payload = RlpTestHelper.EncodeSequence(transactions.Select(static tx => TxDecoder.Instance.Encode(tx)).ToArray());

        byte[] result = protocol.Submit(payload, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(submitted, Is.EqualTo(transactions.Select(static tx => tx.Hash)));
            Assert.That(result, Is.EqualTo(Convert.FromHexString("d2c180c101c102c103c104c105c106c107c108")));
        }
    }

    [TestCaseSource(nameof(MalformedBatches))]
    public void Submit_rejects_malformed_batches_before_pool_admission(byte[] payload)
    {
        using BrowserProtocol protocol = CreateProtocol(out ITxPool pool);

        Assert.That(() => protocol.Submit(payload, CancellationToken.None), Throws.InstanceOf<RlpReaderException>());
        pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    private static IEnumerable<TestCaseData> MalformedBatches()
    {
        byte[] validItem = TxDecoder.Instance.Encode(SignedTransaction(TxType.Legacy));
        byte[] validBatch = RlpTestHelper.EncodeSequence(validItem);
        byte[] unsigned = TxDecoder.Instance.Encode(TxBuilder.A.Transaction.TestObject);
        byte[] typed = TxDecoder.Instance.Encode(SignedTransaction(TxType.EIP1559), RlpBehaviors.SkipTypedWrapping);
        byte[] typedWithTrailingData = [.. typed, Rlp.EmptyByteArrayByte];
        yield return new TestCaseData(validBatch[..^1]).SetName("Submit rejects a truncated batch");
        yield return new TestCaseData((byte[])[.. validBatch, Rlp.EmptyByteArrayByte]).SetName("Submit rejects bytes after the batch");
        yield return new TestCaseData(RlpTestHelper.EncodeSequence(validItem, unsigned)).SetName("Submit rejects an unsigned transaction");
        yield return new TestCaseData(RlpTestHelper.EncodeSequence(validItem, Rlp.Encode(typedWithTrailingData))).SetName("Submit rejects bytes after a typed payload");
        yield return new TestCaseData(RlpTestHelper.EncodeSequence(validItem, Rlp.Encode(new byte[] { (byte)TxType.FrameTx, Rlp.EmptyListByte })))
            .SetName("Submit rejects transaction types outside webtx/1");
    }

    [Test]
    public void Blob_without_a_sidecar_rejects_only_that_transaction([Values] bool withSidecar)
    {
        using BrowserProtocol protocol = CreateProtocol(out ITxPool pool);
        Transaction blob = TxBuilder.A.Transaction.WithShardBlobTxTypeAndFields(isMempoolTx: withSidecar).Signed().TestObject;
        byte[] legacyItem = TxDecoder.Instance.Encode(SignedTransaction(TxType.Legacy));
        byte[] blobItem = TxDecoder.Instance.Encode(blob, withSidecar ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);
        byte[] payload = RlpTestHelper.EncodeSequence(blobItem, legacyItem);

        byte[] response = protocol.Submit(payload, CancellationToken.None);

        Assert.That(response, Is.EqualTo(new byte[] { 0xc4, 0xc1, withSidecar ? (byte)0x80 : (byte)8, 0xc1, 0x80 }));
        pool.Received(withSidecar ? 1 : 0).SubmitTx(Arg.Is<Transaction>(tx => tx.Type == TxType.Blob), TxHandlingOptions.None);
        pool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => tx.Type == TxType.Legacy), TxHandlingOptions.None);
    }

    [Test]
    public void Submit_limits_the_number_of_transactions([Values(0, 15, 16, 17)] int count)
    {
        using BrowserProtocol protocol = CreateProtocol(out ITxPool pool);
        byte[] item = TxDecoder.Instance.Encode(SignedTransaction(TxType.Legacy));
        byte[] payload = RlpTestHelper.EncodeSequence(Enumerable.Repeat(item, count).ToArray());

        if (count > 16)
        {
            Assert.That(() => protocol.Submit(payload, CancellationToken.None), Throws.TypeOf<RlpLimitException>());
            pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            return;
        }

        byte[] response = protocol.Submit(payload, CancellationToken.None);

        Assert.That(response, Is.EqualTo(RlpTestHelper.EncodeSequence(Enumerable.Repeat(new byte[] { 0xc1, 0x80 }, count).ToArray())));
        pool.Received(count).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void Submit_limits_the_complete_encoded_message([Values(-1, 0, 1)] int sizeDelta)
    {
        using BrowserProtocol protocol = CreateProtocol(out ITxPool pool);
        int size = BrowserProtocol.MaxSubmitSize + sizeDelta;
        Transaction tx = TxBuilder.A.Transaction.WithData(new byte[size]).Signed().TestObject;
        byte[] payload = RlpTestHelper.EncodeSequence(TxDecoder.Instance.Encode(tx));
        tx.Data = new byte[2 * size - payload.Length];
        payload = RlpTestHelper.EncodeSequence(TxDecoder.Instance.Encode(tx));
        Assert.That(payload, Has.Length.EqualTo(size));

        if (sizeDelta > 0)
        {
            Assert.That(() => protocol.Submit(payload, CancellationToken.None), Throws.TypeOf<RlpLimitException>());
            pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            return;
        }

        Assert.That(protocol.Submit(payload, CancellationToken.None), Is.EqualTo(new byte[] { 0xc2, 0xc1, 0x80 }));
        pool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
    }

    [Test]
    public void Discovery_response_contains_complete_signed_rlp_records_in_order()
    {
        NodeRecord first = SignedRecord(TestItem.PrivateKeyA, 1);
        NodeRecord second = SignedRecord(TestItem.PrivateKeyB, 2);
        byte[] response = BrowserProtocol.EncodePeerRecords([first, second]);
        RlpReader reader = new(response);
        RlpReader items = reader.EnterSequence(out int length);
        byte[] firstEncoded = items.ReadEncodedItem().ToArray();
        byte[] secondEncoded = items.ReadEncodedItem().ToArray();
        reader.ExitSequence(in items, length);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.HasNext, Is.False);
            Assert.That(firstEncoded, Is.EqualTo(first.ToRlpBytes()));
            Assert.That(secondEncoded, Is.EqualTo(second.ToRlpBytes()));
            Assert.That(NodeRecord.FromBytes(firstEncoded).EnrSequence, Is.EqualTo(1));
            Assert.That(NodeRecord.FromBytes(secondEncoded).EnrSequence, Is.EqualTo(2));
        }
    }

    private static BrowserProtocol CreateProtocol(out ITxPool pool)
    {
        IProtectedPrivateKey nodeKey = Substitute.For<IProtectedPrivateKey>();
        nodeKey.Unprotect().Returns(static _ => new PrivateKey(TestItem.PrivateKeyA.KeyBytes));
        pool = Substitute.For<ITxPool>();
        return new BrowserProtocol(new EthereumEcdsa(1), nodeKey, pool);
    }

    private static Transaction SignedTransaction(TxType type, int nonce = 0) =>
        TxBuilder.A.Transaction.WithType(type).WithNonce(nonce).Signed().TestObject;

    private static NodeRecord SignedRecord(PrivateKey key, ulong sequence)
    {
        NodeRecord record = new() { EnrSequence = sequence };
        record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
        new NodeRecordSigner(new Ecdsa(), key).Sign(record);
        return record;
    }
}
