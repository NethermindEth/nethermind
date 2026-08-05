// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[TestFixture, Parallelizable(ParallelScope.All)]
public class BlockBodiesMessageSerializerTests
{
    [TestCaseSource(nameof(GetBlockBodyValues))]
    public void Should_pass_roundtrip(BlockBody[] bodies) => SerializerTester.TestZero(
        new BlockBodiesMessageSerializer(),
        new BlockBodiesMessage(bodies));

    [TestCaseSource(nameof(GetEncodingCases))]
    public void Should_encode_to_expected_wire_bytes(BlockBody[] bodies, string expected) => SerializerTester.TestZero(
        new BlockBodiesMessageSerializer(),
        new BlockBodiesMessage(bodies),
        expected);

    private static IEnumerable<TestCaseData> GetEncodingCases()
    {
        // The transaction encoding is a known constant: explicit fields and a fixed
        // signature, identical to the TransactionsMessageSerializerTests golden.
        Transaction tx = new()
        {
            Data = new byte[] { 1, 2, 3 },
            GasLimit = 10,
            GasPrice = 100,
            Nonce = 1000,
            Signature = new Signature(1, 2, 27),
            To = TestItem.AddressA,
            Value = 10000
        };

        // A body without withdrawals encodes as the two-item list [transactions, uncles].
        yield return new TestCaseData(
            new BlockBody[] { new(new[] { tx }, [], null) },
            "e8e7e5e48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102c0")
            .SetName(nameof(Should_encode_to_expected_wire_bytes) + "_body_without_withdrawals");

        // A withdrawal encodes as [index, validatorIndex, address, amount] (EIP-4895).
        // The three numeric fields hold distinct values, so a field transposition changes the bytes.
        yield return new TestCaseData(
            new BlockBody[]
            {
                new(new[] { tx }, [],
                    new[] { Build.A.Withdrawal.WithIndex(1).WithValidatorIndex(2).WithAmount(3).WithRecipient(TestItem.AddressA).TestObject })
            },
            "f843f841e5e48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102c0d9d8010294b7705ae4c6f81b66cdb323c65f4e8133690fc09903")
            .SetName(nameof(Should_encode_to_expected_wire_bytes) + "_body_with_withdrawal");
    }

    [TestCaseSource(nameof(GetBlockBodyValues))]
    public void Should_not_contain_network_form_tx_wrapper(BlockBody[] bodies)
    {
        using DisposableByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16).AsDisposable();
        BlockBodiesMessageSerializer serializer = new();
        serializer.Serialize(buffer, new BlockBodiesMessage(bodies));
        using BlockBodiesMessage deserializedMessage = serializer.Deserialize(buffer);
        foreach (BlockBody? body in deserializedMessage.Bodies.Bodies)
        {
            if (body is null) continue;
            foreach (Transaction tx in body.Transactions.Where(static t => t.SupportsBlobs))
            {
                Assert.That(tx.NetworkWrapper, Is.Null);
            }
        }
    }

    private static IEnumerable<BlockBody[]> GetBlockBodyValues()
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia), TestItem.PrivateKeyA)
            .TestObject;

        tx.SenderAddress = null;

        // null body
        yield return new BlockBody[] { null };

        // body with null withdrawals
        yield return new BlockBody[] { new(new[] { tx }, [], null) };

        yield return new BlockBody[]
        {
            // body with empty withdrawals
            new(new[] { tx }, new[] { header }, []),
            // body with a single withdrawals
            new(new[] { tx }, [],
                new[]
                {
                    Build.A.Withdrawal
                        .WithIndex(1)
                        .WithAmount(1)
                        .WithRecipient(TestItem.AddressA)
                        .TestObject
                }),
            // body with multiple withdrawals
            new(new[]
                {
                    Build.A.Transaction
                        .WithChainId(TestBlockchainIds.ChainId)
                        .WithTo(TestItem.AddressA)
                        .WithShardBlobTxTypeAndFields(1)
                        .SignedAndResolved(new EthereumEcdsa(TestBlockchainIds.ChainId),
                            TestItem.PrivateKeyA)
                        .TestObject,
                    Build.A.Transaction
                        .WithChainId(TestBlockchainIds.ChainId)
                        .WithTo(TestItem.AddressA)
                        .WithShardBlobTxTypeAndFields(2)
                        .SignedAndResolved(new EthereumEcdsa(TestBlockchainIds.ChainId),
                            TestItem.PrivateKeyA)
                        .TestObject,
                    Build.A.Transaction
                        .WithChainId(TestBlockchainIds.ChainId)
                        .WithTo(TestItem.AddressA)
                        .WithShardBlobTxTypeAndFields(3, false)
                        .SignedAndResolved(new EthereumEcdsa(TestBlockchainIds.ChainId),
                            TestItem.PrivateKeyA)
                        .TestObject,
                }, new[] { header },
                new[]
                {
                    Build.A.Withdrawal
                        .WithIndex(1)
                        .WithAmount(1)
                        .WithRecipient(TestItem.AddressA)
                        .TestObject,
                    Build.A.Withdrawal
                        .WithIndex(2)
                        .WithAmount(2)
                        .WithRecipient(TestItem.AddressB)
                        .TestObject,
                    Build.A.Withdrawal
                        .WithIndex(3)
                        .WithAmount(3)
                        .WithRecipient(TestItem.AddressC)
                        .TestObject
                }),
        };
    }
}
