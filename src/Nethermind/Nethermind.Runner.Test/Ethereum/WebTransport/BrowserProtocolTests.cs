// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable annotations

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Runner.Ethereum.WebTransport;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using TxBuilder = Nethermind.Core.Test.Builders.Build;

namespace Nethermind.Runner.Test.Ethereum.WebTransport;

public class BrowserProtocolTests
{
    [TestCase((byte)BrowserCapability.WebTx, "webtx/1")]
    [TestCase((byte)BrowserCapability.WebDisc, "webdisc/1")]
    public void Handshake_signs_the_geth_challenge_with_the_enr_key(byte selected, string capability)
    {
        using BrowserProtocol protocol = CreateProtocol();
        byte[] nonce = [.. Enumerable.Range(0, BrowserProtocol.NonceLength).Select(static i => (byte)i)];
        byte[] handshake = protocol.CreateHandshake((BrowserCapability)selected, nonce);
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
        WebTxHandler handler = CreateTxHandler(out ITxPool pool);
        AcceptTxResult[] outcomes =
        [
            AcceptTxResult.Accepted, AcceptTxResult.AlreadyKnown, AcceptTxResult.OldNonce,
            AcceptTxResult.ReplacementNotAllowed, AcceptTxResult.FeeTooLow, AcceptTxResult.InsufficientFunds,
            AcceptTxResult.GasLimitExceeded.WithMessage("detail"), AcceptTxResult.Syncing, AcceptTxResult.FailedToResolveSender
        ];
        List<Hash256?> submitted = [];
        pool.SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.RequireLocalAdmission).Returns(call =>
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

        byte[] result = handler.Handle(payload, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(submitted, Is.EqualTo(transactions.Select(static tx => tx.Hash)));
            Assert.That(result, Is.EqualTo(Convert.FromHexString("d2c180c101c102c103c104c105c106c107c108")));
        }
    }

    [TestCaseSource(nameof(MalformedBatches))]
    public void Submit_rejects_malformed_batches_before_pool_admission(byte[] payload)
    {
        WebTxHandler handler = CreateTxHandler(out ITxPool pool);

        Assert.That(() => handler.Handle(payload, CancellationToken.None), Throws.InstanceOf<RlpReaderException>());
        pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
    }

    [Test]
    public async Task Submit_reports_the_specific_reasons_from_transaction_validation()
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance))
            .WithGenesisPostProcessor(static (genesis, _) => genesis.Header.GasLimit = 30_000_000));
        WebTxHandler handler = new(chain.TxPool);
        IEthereumEcdsa wrongChain = new EthereumEcdsa(chain.SpecProvider.ChainId + 1);
        Transaction[] transactions =
        [
            Tx().WithNonce(ulong.MaxValue).Signed(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Tx().WithType(TxType.EIP1559).WithMaxFeePerGas(2_000_000_000).WithMaxPriorityFeePerGas(3_000_000_000)
                .Signed(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Tx().WithGasLimit(20_999).Signed(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Tx().WithData([1]).WithGasLimit(21_016).Signed(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Tx().WithGasLimit(Eip7825Constants.DefaultTxGasLimitCap + 1).Signed(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Tx().WithType(TxType.EIP1559).WithGasLimit(20_999).WithMaxFeePerGas(2_000_000_000).WithMaxPriorityFeePerGas(1_000_000_000)
                .Signed(wrongChain, TestItem.PrivateKeyA).TestObject,
        ];
        byte[] payload = RlpTestHelper.EncodeSequence(transactions.Select(static tx => TxDecoder.Instance.Encode(tx)).ToArray());

        Assert.That(handler.Handle(payload, CancellationToken.None), Is.EqualTo(Convert.FromHexString("ccc102c103c106c106c106c108")));

        static TransactionBuilder<Transaction> Tx() => TxBuilder.A.Transaction.WithTo(TestItem.AddressB)
            .WithValue(0).WithGasLimit(21_000).WithGasPrice(10_000_000_000);
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
        WebTxHandler handler = CreateTxHandler(out ITxPool pool);
        Transaction blob = TxBuilder.A.Transaction.WithShardBlobTxTypeAndFields(isMempoolTx: withSidecar).Signed().TestObject;
        byte[] legacyItem = TxDecoder.Instance.Encode(SignedTransaction(TxType.Legacy));
        byte[] blobItem = TxDecoder.Instance.Encode(blob, withSidecar ? RlpBehaviors.InMempoolForm : RlpBehaviors.None);
        byte[] payload = RlpTestHelper.EncodeSequence(blobItem, legacyItem);

        byte[] response = handler.Handle(payload, CancellationToken.None);

        Assert.That(response, Is.EqualTo(new byte[] { 0xc4, 0xc1, withSidecar ? (byte)0x80 : (byte)8, 0xc1, 0x80 }));
        pool.Received(withSidecar ? 1 : 0).SubmitTx(Arg.Is<Transaction>(tx => tx.Type == TxType.Blob), TxHandlingOptions.RequireLocalAdmission);
        pool.Received(1).SubmitTx(Arg.Is<Transaction>(tx => tx.Type == TxType.Legacy), TxHandlingOptions.RequireLocalAdmission);
    }

    [Test]
    public void Submit_limits_the_number_of_transactions([Values(0, 15, 16, 17)] int count)
    {
        WebTxHandler handler = CreateTxHandler(out ITxPool pool);
        byte[] item = TxDecoder.Instance.Encode(SignedTransaction(TxType.Legacy));
        byte[] payload = RlpTestHelper.EncodeSequence(Enumerable.Repeat(item, count).ToArray());

        if (count > 16)
        {
            Assert.That(() => handler.Handle(payload, CancellationToken.None), Throws.TypeOf<RlpLimitException>());
            pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            return;
        }

        byte[] response = handler.Handle(payload, CancellationToken.None);

        Assert.That(response, Is.EqualTo(RlpTestHelper.EncodeSequence(Enumerable.Repeat(new byte[] { 0xc1, 0x80 }, count).ToArray())));
        pool.Received(count).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.RequireLocalAdmission);
    }

    [Test]
    public void Submit_limits_the_complete_encoded_message([Values(-1, 0, 1)] int sizeDelta)
    {
        WebTxHandler handler = CreateTxHandler(out ITxPool pool);
        int size = WebTxHandler.MaxRequestSize + sizeDelta;
        Transaction tx = TxBuilder.A.Transaction.WithData(new byte[size]).Signed().TestObject;
        byte[] payload = RlpTestHelper.EncodeSequence(TxDecoder.Instance.Encode(tx));
        tx.Data = new byte[2 * size - payload.Length];
        payload = RlpTestHelper.EncodeSequence(TxDecoder.Instance.Encode(tx));
        Assert.That(payload, Has.Length.EqualTo(size));

        if (sizeDelta > 0)
        {
            Assert.That(() => handler.Handle(payload, CancellationToken.None), Throws.TypeOf<RlpLimitException>());
            pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            return;
        }

        Assert.That(handler.Handle(payload, CancellationToken.None), Is.EqualTo(new byte[] { 0xc2, 0xc1, 0x80 }));
        pool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.RequireLocalAdmission);
    }

    [Test]
    public void Submit_reports_pool_error_when_the_node_has_no_transaction_pool([Values] bool decorated)
    {
        ITxPool pool = NullTxPool.Instance;
        if (decorated)
        {
            pool = Substitute.For<ITxPool>();
            pool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>()).Returns(static call =>
                NullTxPool.Instance.SubmitTx(call.Arg<Transaction>(), call.Arg<TxHandlingOptions>()));
        }

        WebTxHandler handler = new(pool);
        Transaction transaction = SignedTransaction(TxType.Legacy);
        byte[] payload = RlpTestHelper.EncodeSequence(TxDecoder.Instance.Encode(transaction));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.Handle(payload, CancellationToken.None), Is.EqualTo(new byte[] { 0xc2, 0xc1, 7 }));
            Assert.That(pool.SubmitTx(transaction, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }
    }

    [Test]
    public async Task Discovery_returns_self_and_only_current_verified_quic_peers([Values] bool stale)
    {
        WebDiscHandler handler = CreateDiscHandler(out INodeRecordProvider records, out IPeerPool peerPool);
        NodeRecord self = SignedRecord(TestItem.PrivateKeyA, 1);
        NodeRecord second = SignedRecord(TestItem.PrivateKeyB, 2);
        records.GetCurrentAsync().Returns(self);
        Node peer = new(TestItem.PrivateKeyB.PublicKey, new IPEndPoint(IPAddress.Loopback, 30303));
        Assert.That(peer.SetVerifiedEnr(second), Is.True);
        peerPool.Peers[peer.Id] = new Peer(peer);

        byte[] request = [Rlp.EmptyListByte];
        byte[] initial = await handler.HandleAsync(request, CancellationToken.None);
        if (stale)
            peer.ObserveEnrSequence(3);

        byte[] response = await handler.HandleAsync(request, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(initial, Is.EqualTo(RlpTestHelper.EncodeSequence(self.ToRlpBytes(), second.ToRlpBytes())));
            Assert.That(response, Is.EqualTo(stale ? RlpTestHelper.EncodeSequence(self.ToRlpBytes()) : initial));
        }
    }

    [TestCase("")]
    [TestCase("80")]
    [TestCase("c0c0")]
    public void Discovery_rejects_requests_other_than_an_empty_list(string hex)
    {
        WebDiscHandler handler = CreateDiscHandler(out INodeRecordProvider records, out _);

        Assert.That(async () => await handler.HandleAsync(Convert.FromHexString(hex), CancellationToken.None), Throws.TypeOf<InvalidDataException>());
        _ = records.DidNotReceive().GetCurrentAsync(Arg.Any<CancellationToken>());
    }

    private static BrowserProtocol CreateProtocol()
    {
        IProtectedPrivateKey nodeKey = Substitute.For<IProtectedPrivateKey>();
        nodeKey.Unprotect().Returns(static _ => new PrivateKey(TestItem.PrivateKeyA.KeyBytes));
        return new BrowserProtocol(new EthereumEcdsa(1), nodeKey, CreateTxHandler(out _), CreateDiscHandler(out _, out _));
    }

    private static WebTxHandler CreateTxHandler(out ITxPool pool)
    {
        pool = Substitute.For<ITxPool>();
        return new WebTxHandler(pool);
    }

    private static WebDiscHandler CreateDiscHandler(out INodeRecordProvider records, out IPeerPool peerPool)
    {
        IIPResolver resolver = Substitute.For<IIPResolver>();
        resolver.Resolve(Arg.Any<CancellationToken>()).Returns(new IIPResolver.NethermindIp(IPAddress.Loopback, IPAddress.Loopback));
        NetworkListenerState listenerState = new(new NetworkConfig(), resolver, LimboLogs.Instance);
        listenerState.SetWebTransport(new(IPAddress.Loopback, 30304, new byte[64]));
        records = Substitute.For<INodeRecordProvider>();
        peerPool = Substitute.For<IPeerPool>();
        peerPool.Peers.Returns(new ConcurrentDictionary<PublicKeyAsKey, Peer>());
        return new WebDiscHandler(listenerState, records, peerPool);
    }

    private static Transaction SignedTransaction(TxType type, int nonce = 0) =>
        TxBuilder.A.Transaction.WithType(type).WithNonce(nonce).Signed().TestObject;

    private static NodeRecord SignedRecord(PrivateKey key, ulong sequence)
    {
        NodeRecord record = new() { EnrSequence = sequence };
        record.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
        record.SetEntry(new IpEntry(IPAddress.Loopback));
        record.SetEntry(new QuicPortEntry(30304));
        record.SetEntry(new QuicCertificateHashesEntry(new byte[64]));
        new NodeRecordSigner(new Ecdsa(), key).Sign(record);
        return record;
    }
}
