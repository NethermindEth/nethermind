// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class BlockDecoderTests
{
    private static readonly Block[] _scenarios = BuildScenarios();

    private static Block[] BuildScenarios()
    {
        Transaction[] transactions = new Transaction[100];
        for (uint i = 0; i < transactions.Length; i++)
        {
            transactions[i] = Build.A.Transaction
                .WithData([(byte)i])
                .WithNonce(i)
                .WithValue((UInt256)i)
                .Signed(new EthereumEcdsa(TestBlockchainIds.ChainId), TestItem.PrivateKeyA, true)
                .TestObject;
        }

        BlockHeader[] uncles = new BlockHeader[2];

        for (int i = 0; i < uncles.Length; i++)
        {
            uncles[i] = Build.A.BlockHeader
                .WithWithdrawalsRoot(i % 3 == 0 ? null : Keccak.Compute(i.ToString()))
                .TestObject;
        }

        return
        [
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithTransactions(transactions)
                .WithUncles(Build.A.BlockHeader.TestObject)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(0)
                .WithExcessBlobGas(0)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(0xff)
                .WithExcessBlobGas(0xff)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block
                .WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(ulong.MaxValue)
                .WithExcessBlobGas(ulong.MaxValue)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            Build.A.Block.WithNumber(1)
                .WithBaseFeePerGas(1)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBlobGasUsed(ulong.MaxValue)
                .WithExcessBlobGas(ulong.MaxValue)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject,
            // This scenario sets every optional tail field, with a distinct value per field. This makes the field comparisons discriminating.
            Build.A.Block.WithNumber(1)
                .WithBaseFeePerGas(3)
                .WithTransactions(transactions)
                .WithUncles(uncles)
                .WithWithdrawals(8)
                .WithBloom(new Bloom([Build.A.LogEntry.TestObject]))
                .WithBlobGasUsed(1)
                .WithExcessBlobGas(2)
                .WithParentBeaconBlockRoot(TestItem.KeccakA)
                .WithRequestsHash(TestItem.KeccakB)
                .WithBlockAccessListHash(TestItem.KeccakC)
                .WithSlotNumber(7)
                .WithMixHash(Keccak.EmptyTreeHash)
                .TestObject
        ];
    }

    private static IEnumerable<Block> BlockScenarios() => _scenarios;

    private static IEnumerable<Block> BlockScenariosWithTxs() =>
        _scenarios.Where(static b => b.Transactions.Length > 0);

    [Test]
    public void Can_do_roundtrip_null()
    {
        BlockDecoder decoder = new();
        Rlp result = decoder.Encode((Block?)null);
        Block? decoded = Rlp.Decode<Block>(result.Bytes.AsSpan());
        Assert.That(decoded, Is.Null);
    }

    private readonly string regression5644 = "f902cff9025aa05297f2a4a699ba7d038a229a8eb7ab29d0073b37376ff0311f2bd9c608411830a01dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347940000000000000000000000000000000000000000a0fe77dd4ad7c2a3fa4c11868a00e4d728adcdfef8d2e3c13b256b06cbdbb02ec9a00d0abe08c162e4e0891e7a45a8107a98ae44ed47195c2d041fe574de40272df0a0056b23fbba480696b65fe5a59b8f2148a1299103c4f57df839233af2cf4ca2d2b90100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000182160c837a1200825208845c54648eb8613078366336393733363936653733366236390000000000000000000000000000f3ec96e458292ccea72a1e53e95f94c28051ab51880b7e03d933f7fa78c9692f635ae55ac3899c9c6999d33c758b5248a05894a3471282333bcd76067c5d391300a00000000000000000000000000000000000000000000000000000000000000000880000000000000000f86ff86d80843b9aca008252089422ea9f6b28db76a7162054c05ed812deb2f519cd8a152d02c7e14af6800000802da0f67424c67d9f91a87b5437db1bdaa05e29bd020ab474b2f67f7be163c9f650dda02f90ab34b44165d776ae04449b15210076d6a72abe2bda2903d4b87f0d1ce541c0";

    [Test]
    public void Can_do_roundtrip_regression()
    {
        BlockDecoder decoder = new();

        byte[] bytes = Bytes.FromHexString(regression5644);
        RlpReader ctx = new(bytes);
        Block? decoded = decoder.Decode(ref ctx);
        Rlp encoded = decoder.Encode(decoded);

        // An independent pyrlp decode of the fixed bytes produced the expected values.
        // Only this test decodes canonical wire. Only these asserts can find a self-canceling encode/decode error.
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.Transactions, Has.Length.EqualTo(1));
        Transaction tx = decoded.Transactions[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded.Bytes.ToHexString(), Is.EqualTo(bytes.ToHexString()));
            Assert.That(decoded.Header.Number, Is.EqualTo(5644UL));
            Assert.That(decoded.Header.Difficulty, Is.EqualTo(UInt256.One));
            Assert.That(decoded.Header.Beneficiary, Is.EqualTo(Address.Zero));
            Assert.That(decoded.Header.GasLimit, Is.EqualTo(8_000_000UL));
            Assert.That(decoded.Header.GasUsed, Is.EqualTo(21_000UL));
            Assert.That(decoded.Header.Timestamp, Is.EqualTo(1549034638UL));
            Assert.That(decoded.Header.StateRoot, Is.EqualTo(new Hash256("0xfe77dd4ad7c2a3fa4c11868a00e4d728adcdfef8d2e3c13b256b06cbdbb02ec9")));
            Assert.That(tx.Nonce, Is.EqualTo(0UL));
            Assert.That(tx.GasPrice, Is.EqualTo((UInt256)1_000_000_000));
            Assert.That(tx.GasLimit, Is.EqualTo(21_000UL));
            Assert.That(tx.To, Is.EqualTo(new Address("0x22ea9f6b28db76a7162054c05ed812deb2f519cd")));
            Assert.That(tx.Value, Is.EqualTo(UInt256.Parse("100000000000000000000000")));
            Assert.That(decoded.Uncles, Is.Empty);
        }
    }

    [Test]
    public void Can_do_roundtrip_scenarios(
        [ValueSource(nameof(BlockScenarios))] Block block)
    {
        BlockDecoder decoder = new();
        Rlp encoded = decoder.Encode(block);
        RlpReader ctx = new(encoded.Bytes);
        Block? decoded = decoder.Decode(ref ctx);
        Rlp encoded2 = decoder.Encode(decoded);

        // A re-encode comparison cannot find a decode error that the matching encode hides. The field
        // comparisons below can. A fully self-canceling encode/decode pair passes both; only a test
        // that decodes canonical wire, like Can_do_roundtrip_regression, can find that class.
        Assert.That(decoded, Is.Not.Null);
        Assert.That(decoded!.Transactions, Has.Length.EqualTo(block.Transactions.Length));
        Assert.That(decoded.Uncles, Has.Length.EqualTo(block.Uncles.Length));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded2.Bytes.ToHexString(), Is.EqualTo(encoded.Bytes.ToHexString()));
            // The hash pins the embedded header bytes against the standalone header encoding. It cannot find decode errors.
            Assert.That(decoded.Hash, Is.EqualTo(block.Hash));

            BlockHeader actual = decoded.Header;
            BlockHeader expected = block.Header;
            Assert.That(actual.ParentHash, Is.EqualTo(expected.ParentHash));
            Assert.That(actual.UnclesHash, Is.EqualTo(expected.UnclesHash));
            Assert.That(actual.Beneficiary, Is.EqualTo(expected.Beneficiary));
            Assert.That(actual.StateRoot, Is.EqualTo(expected.StateRoot));
            Assert.That(actual.TxRoot, Is.EqualTo(expected.TxRoot));
            Assert.That(actual.ReceiptsRoot, Is.EqualTo(expected.ReceiptsRoot));
            Assert.That(actual.Bloom, Is.EqualTo(expected.Bloom));
            Assert.That(actual.Difficulty, Is.EqualTo(expected.Difficulty));
            Assert.That(actual.Number, Is.EqualTo(expected.Number));
            Assert.That(actual.GasLimit, Is.EqualTo(expected.GasLimit));
            Assert.That(actual.GasUsed, Is.EqualTo(expected.GasUsed));
            Assert.That(actual.Timestamp, Is.EqualTo(expected.Timestamp));
            Assert.That(actual.ExtraData, Is.EqualTo(expected.ExtraData));
            Assert.That(actual.Nonce, Is.EqualTo(expected.Nonce));
            Assert.That(actual.BaseFeePerGas, Is.EqualTo(expected.BaseFeePerGas));
            Assert.That(actual.WithdrawalsRoot, Is.EqualTo(expected.WithdrawalsRoot));
            Assert.That(actual.BlobGasUsed, Is.EqualTo(expected.BlobGasUsed));
            Assert.That(actual.ExcessBlobGas, Is.EqualTo(expected.ExcessBlobGas));
            Assert.That(actual.MixHash, Is.EqualTo(expected.MixHash));
            Assert.That(actual.ParentBeaconBlockRoot, Is.EqualTo(expected.ParentBeaconBlockRoot));
            Assert.That(actual.RequestsHash, Is.EqualTo(expected.RequestsHash));
            Assert.That(actual.BlockAccessListHash, Is.EqualTo(expected.BlockAccessListHash));
            Assert.That(actual.SlotNumber, Is.EqualTo(expected.SlotNumber));

            for (int i = 0; i < block.Transactions.Length; i++)
            {
                Assert.That(decoded.Transactions[i].Hash, Is.EqualTo(block.Transactions[i].Hash), $"transaction {i}");
            }

            for (int i = 0; i < block.Uncles.Length; i++)
            {
                Assert.That(decoded.Uncles[i].Hash, Is.EqualTo(block.Uncles[i].Hash), $"uncle {i}");
            }

            Assert.That(decoded.Withdrawals?.Length, Is.EqualTo(block.Withdrawals?.Length));
        }
    }

    [Test]
    public void Encode_with_pre_encoded_transactions_produces_same_rlp(
        [ValueSource(nameof(BlockScenariosWithTxs))] Block block)
    {
        byte[][] encodedTxs = new byte[block.Transactions.Length][];
        for (int i = 0; i < block.Transactions.Length; i++)
        {
            encodedTxs[i] = Rlp.Encode(block.Transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        BlockDecoder decoder = new();
        Rlp standard = decoder.Encode(block);

        Block blockWithEncoded = new(block.Header, block.Body) { EncodedTransactions = encodedTxs };
        Rlp fast = decoder.Encode(blockWithEncoded);

        Assert.That(fast.Bytes.ToHexString(), Is.EqualTo(standard.Bytes.ToHexString()));
    }

    [Test]
    public void Encode_with_pre_encoded_typed_transactions_produces_same_rlp()
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithNonce(1).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(TxType.AccessList).Signed().TestObject,
            Build.A.Transaction.WithNonce(3).WithType(TxType.EIP1559).Signed().TestObject,
        ];

        Block block = Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(1)
            .WithTransactions(transactions)
            .WithWithdrawals(2)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithMixHash(Keccak.EmptyTreeHash)
            .TestObject;

        byte[][] encodedTxs = new byte[transactions.Length][];
        for (int i = 0; i < transactions.Length; i++)
        {
            encodedTxs[i] = Rlp.Encode(transactions[i], RlpBehaviors.SkipTypedWrapping).Bytes;
        }

        BlockDecoder decoder = new();
        Rlp standard = decoder.Encode(block);

        Block blockWithEncoded = new(block.Header, block.Body) { EncodedTransactions = encodedTxs };
        Rlp fast = decoder.Encode(blockWithEncoded);

        Assert.That(fast.Bytes.ToHexString(), Is.EqualTo(standard.Bytes.ToHexString()));
    }

    [Test]
    public void Receipt_recovery_hashes_encoded_legacy_and_typed_transactions_without_decoding()
    {
        Transaction[] transactions =
        [
            Build.A.Transaction.WithNonce(1).WithType(TxType.Legacy).Signed().TestObject,
            Build.A.Transaction.WithNonce(2).WithType(TxType.AccessList).Signed().TestObject,
            Build.A.Transaction.WithNonce(3).WithType(TxType.EIP1559).Signed().TestObject,
        ];
        Block block = Build.A.Block
            .WithNumber(1)
            .WithBaseFeePerGas(1)
            .WithTransactions(transactions)
            .WithWithdrawals(2)
            .WithBlobGasUsed(0)
            .WithExcessBlobGas(0)
            .WithMixHash(Keccak.EmptyTreeHash)
            .TestObject;

        BlockDecoder decoder = new();
        byte[] encoded = decoder.Encode(block).Bytes;
        ReceiptRecoveryBlock recovery = decoder.DecodeToReceiptRecoveryBlock(null, encoded, RlpBehaviors.None)
            ?? throw new AssertionException("encoded block should decode for receipt recovery");

        try
        {
            for (int i = 0; i < transactions.Length; i++)
            {
                Assert.That(recovery.GetNextTransactionHash(), Is.EqualTo(transactions[i].Hash), $"transaction {i}");
            }

            Assert.Throws<RlpException>(() => recovery.GetNextTransactionHash());
        }
        finally
        {
            recovery.Dispose();
        }
    }

    [Test]
    public void Receipt_recovery_calculates_a_missing_in_memory_transaction_hash()
    {
        Transaction transaction = Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(1)
            .Signed()
            .TestObject;
        Hash256 expectedHash = transaction.CalculateHash();
        transaction.Hash = null;
        ReceiptRecoveryBlock recovery = new(Build.A.Block.WithTransactions(transaction).TestObject);
        Hash256 actualHash = recovery.GetNextTransactionHash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualHash, Is.EqualTo(expectedHash));
            Assert.That(transaction.Hash, Is.EqualTo(expectedHash), "the calculated hash should be cached on the transaction");
        }
    }

    public static byte[][] MalformedRecoveryTransactions =
    [
        [],
        [0xc0],
        [0x80],
        [0x01],
        [0x81, 0x01],
        [0x82, 0x00, 0xc0],
        [0x82, 0x80, 0xc0],
        [0x82, 0x01],
        [0xc2, 0x80],
        [0xb8, 0x38],
        [0xb8, 0x01, 0x01],
    ];

    [TestCaseSource(nameof(MalformedRecoveryTransactions))]
    public void Receipt_recovery_rejects_malformed_transaction_envelopes(byte[] transactionData)
    {
        ReceiptRecoveryBlock recovery = new(
            null,
            Build.A.BlockHeader.TestObject,
            transactionData,
            transactionCount: 1);

        Assert.Throws<RlpException>(() => recovery.GetNextTransactionHash());
    }

    [Test]
    public void Get_length_null()
    {
        BlockDecoder decoder = new();
        Assert.That(decoder.GetLength((Block?)null, RlpBehaviors.None), Is.EqualTo(1));
    }

    public static byte[][] MalformedInput =
    [
        [0xFF, 0xFF],  // Bug #001 original.
        [0xF8, 0xFF],  // Long list prefix, no length.
        [0xB8, 0xFF],  // Long string prefix, no length.
        "\0\0"u8.ToArray() // Invalid prefix.
    ];

    /// <summary>
    /// Fuzz-generated edge case: Very short truncated input.
    /// </summary>
    [TestCaseSource(nameof(MalformedInput))]
    public void Rejects_malformed_input(byte[] input)
    {
        BlockDecoder decoder = new();
        Assert.Throws<RlpException>(() =>
        {
            RlpReader ctx = new(input);
            decoder.Decode(ref ctx);
        });
    }
}
