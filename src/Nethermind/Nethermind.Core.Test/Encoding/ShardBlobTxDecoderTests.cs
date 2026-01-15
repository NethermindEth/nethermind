// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
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
        RlpStream rlpStream = new RlpStream(_txDecoder.GetLength(testCase.Tx, RlpBehaviors.None));
        _txDecoder.Encode(rlpStream, testCase.Tx);
        rlpStream.Position = 0;
        Transaction? decoded = _txDecoder.Decode(rlpStream);
        decoded!.SenderAddress =
            new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
        decoded.Hash = decoded.CalculateHash();
        decoded.Should().BeEquivalentTo(testCase.Tx, testCase.Description);
    }

    [Test]
    public void TestDecodeTamperedBlob()
    {
        var bytes = Bytes.FromHexString(
            "b8aa03f8a7018001808252089400000000000000000000000000000000000000000180c001f841a00100000000000000000000000000000000000000000000000000000000000000a0010000000000000000000000000000000000000000000000000000000000000080a00fb9ad625df88e2fea9e088b69a31497f0d9b767067db8c03fd2453d7092e7bfa0086f2930db968d992d0fb06ddc903ca5522ba38bedc0530eb28b61082897efa1");
        var stream = new RlpStream(bytes);

        var tryDecode = () => _txDecoder.Decode(stream);
        tryDecode.Should().Throw<RlpException>();
    }

    [TestCaseSource(nameof(TestCaseSource))]
    public void Roundtrip_ValueDecoderContext_ExecutionPayloadForm_for_shard_blobs((Transaction Tx, string Description) testCase)
    {
        RlpStream rlpStream = new(10000);
        _txDecoder.Encode(rlpStream, testCase.Tx);

        Span<byte> spanIncomingTxRlp = rlpStream.Data.AsSpan();
        Rlp.ValueDecoderContext decoderContext = new(spanIncomingTxRlp);
        rlpStream.Position = 0;
        Transaction? decoded = _txDecoder.Decode(ref decoderContext);
        decoded!.SenderAddress =
            new EthereumEcdsa(TestBlockchainIds.ChainId).RecoverAddress(decoded);
        decoded.Hash = decoded.CalculateHash();
        decoded.Should().BeEquivalentTo(testCase.Tx, testCase.Description);
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
        var stream = new RlpStream(_txDecoder.GetLength(tx, RlpBehaviors.None));
        _txDecoder.Encode(stream, tx);
        // Tamper with sequence length
        {
            var itemsLength = 0;
            foreach (var array in tx.BlobVersionedHashes!)
            {
                itemsLength += Rlp.LengthOf(array);
            }

            // Position where it starts encoding `BlobVersionedHashes`
            stream.Position = 37;
            // Accepts `itemsLength - 10` all the way to `itemsLength - 1`
            stream.StartSequence(itemsLength - 1);
        }
        stream.Position = 0;

        // Decoding should fail
        var tryDecode = () => _txDecoder.Decode(stream);
        tryDecode.Should().Throw<RlpException>();
    }

    [TestCaseSource(nameof(ShardBlobTxTests))]
    public void NetworkWrapper_is_decoded_correctly(string rlp, Hash256 signedHash, RlpBehaviors rlpBehaviors)
    {
        RlpStream incomingTxRlp = Bytes.FromHexString(rlp).AsRlpStream();
        byte[] spanIncomingTxRlp = Bytes.FromHexString(rlp);
        Rlp.ValueDecoderContext decoderContext = new(spanIncomingTxRlp.AsSpan());

        Transaction? decoded = _txDecoder.Decode(incomingTxRlp, rlpBehaviors);
        Transaction? decodedByValueDecoderContext = _txDecoder.Decode(ref decoderContext, rlpBehaviors);

        Assert.That(decoded!.Hash, Is.EqualTo(signedHash));
        Assert.That(decodedByValueDecoderContext!.Hash, Is.EqualTo(signedHash));

        if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm)
        {
            Rlp epEncoded = _txDecoder.Encode(decoded!, rlpBehaviors ^ RlpBehaviors.InMempoolForm);
            RlpStream epStream = new(epEncoded.Bytes);
            Transaction? epDecoded = _txDecoder.Decode(epStream, rlpBehaviors ^ RlpBehaviors.InMempoolForm);
            Assert.That(epDecoded!.Hash, Is.EqualTo(signedHash));
        }

        if (decoded is { NetworkWrapper: ShardBlobNetworkWrapper wrapper })
        {
            Assert.That(IBlobProofsManager.For(wrapper.Version).ValidateProofs(wrapper));
        }

        Rlp encoded = _txDecoder.Encode(decoded!, rlpBehaviors);
        Rlp encodedWithDecodedByValueDecoderContext =
            _txDecoder.Encode(decodedByValueDecoderContext!, rlpBehaviors);
        Assert.That(encoded.Bytes, Is.EquivalentTo(spanIncomingTxRlp));
        Assert.That(encodedWithDecodedByValueDecoderContext.Bytes, Is.EquivalentTo(spanIncomingTxRlp));
    }
}
