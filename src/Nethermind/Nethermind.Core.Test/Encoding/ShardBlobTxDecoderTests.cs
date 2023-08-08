// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

[TestFixture]
public partial class ShardBlobTxDecoderTests
{
    private readonly TxDecoder _txDecoder = new();

    [SetUp]
    public static Task SetUp() => KzgPolynomialCommitments.InitializeAsync();

    public static IEnumerable<(Transaction, string)> TestCaseSource() =>
        TxDecoderTests.TestObjectsSource().Select(tos => (tos.Item1
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
            new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance).RecoverAddress(decoded);
        decoded.Hash = decoded.CalculateHash();
        decoded.Should().BeEquivalentTo(testCase.Tx, testCase.Description);
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
            new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance).RecoverAddress(decoded);
        decoded.Hash = decoded.CalculateHash();
        decoded.Should().BeEquivalentTo(testCase.Tx, testCase.Description);
    }

    [TestCaseSource(nameof(ShardBlobTxTests))]
    public void NetworkWrapper_is_decoded_correctly(string rlp, Keccak signedHash, RlpBehaviors rlpBehaviors)
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
            Assert.That(KzgPolynomialCommitments.AreProofsValid(
                wrapper.Blobs,
                wrapper.Commitments,
                wrapper.Proofs));
        }

        Rlp encoded = _txDecoder.Encode(decoded!, rlpBehaviors);
        Rlp encodedWithDecodedByValueDecoderContext =
            _txDecoder.Encode(decodedByValueDecoderContext!, rlpBehaviors);
        Assert.That(encoded.Bytes, Is.EquivalentTo(spanIncomingTxRlp));
        Assert.That(encodedWithDecodedByValueDecoderContext.Bytes, Is.EquivalentTo(spanIncomingTxRlp));
    }
}
