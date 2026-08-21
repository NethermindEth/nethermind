// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class LightTxDecoderTests
{
    [TestCase(ProofVersion.V0, (byte)0x80)]
    [TestCase(ProofVersion.V1, (byte)0x01)]
    public void Should_roundtrip_proof_version(ProofVersion version, byte trailingByte)
    {
        Transaction tx = BuildBlobTx(version);

        byte[] encoded = LightTxDecoder.Encode(tx);
        LightTransaction decoded = LightTxDecoder.Decode(encoded);

        // Pinned so the fix stays on the read side: already-persisted records carry these exact bytes.
        Assert.That(encoded[^1], Is.EqualTo(trailingByte));
        Assert.That(decoded.GetProofVersion(), Is.EqualTo(version));
        AssertCommonFields(decoded, tx);
    }

    [Test]
    public void Should_decode_record_written_without_proof_version()
    {
        Transaction tx = BuildBlobTx(ProofVersion.V0);

        // Records persisted before the proof version field was appended end after the size field
        byte[] encoded = LightTxDecoder.Encode(tx);
        LightTransaction decoded = LightTxDecoder.Decode(encoded[..^1]);

        Assert.That(decoded.GetProofVersion(), Is.EqualTo(ProofVersion.V0));
        AssertCommonFields(decoded, tx);
    }

    private static Transaction BuildBlobTx(ProofVersion version)
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithNonce(3)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Mainnet), TestItem.PrivateKeyA).TestObject;

        tx.PoolIndex = 7;
        tx.NetworkWrapper = ((ShardBlobNetworkWrapper)tx.NetworkWrapper!) with { Version = version };
        return tx;
    }

    private static void AssertCommonFields(LightTransaction decoded, Transaction expected) =>
        Assert.Multiple(() =>
        {
            Assert.That(decoded.Hash, Is.EqualTo(expected.Hash));
            Assert.That(decoded.SenderAddress, Is.EqualTo(expected.SenderAddress));
            Assert.That(decoded.Nonce, Is.EqualTo(expected.Nonce));
            Assert.That(decoded.Value, Is.EqualTo(expected.Value));
            Assert.That(decoded.GasLimit, Is.EqualTo(expected.GasLimit));
            Assert.That(decoded.GasPrice, Is.EqualTo(expected.GasPrice));
            Assert.That(decoded.DecodedMaxFeePerGas, Is.EqualTo(expected.DecodedMaxFeePerGas));
            Assert.That(decoded.MaxFeePerBlobGas, Is.EqualTo(expected.MaxFeePerBlobGas));
            Assert.That(decoded.BlobVersionedHashes, Is.EqualTo(expected.BlobVersionedHashes));
            Assert.That(decoded.Timestamp, Is.EqualTo(expected.Timestamp));
            Assert.That(decoded.PoolIndex, Is.EqualTo(expected.PoolIndex));
            Assert.That(decoded.GetLength(), Is.EqualTo(expected.GetLength()));
        });
}
