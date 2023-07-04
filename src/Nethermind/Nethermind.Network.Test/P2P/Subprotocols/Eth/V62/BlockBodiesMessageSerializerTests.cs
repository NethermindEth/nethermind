// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[TestFixture, Parallelizable(ParallelScope.All)]
public class BlockBodiesMessageSerializerTests
{
    [TestCaseSource(nameof(GetBlockBodyValues))]
    public void Should_pass_roundtrip(BlockBody[] bodies) => SerializerTester.TestZero(
        new BlockBodiesMessageSerializer(),
        new BlockBodiesMessage { Bodies = bodies },
        additionallyExcluding: (o) =>
            o.Excluding(c => c.Name == nameof(Transaction.SenderAddress))
                .Excluding(c => c.Name == nameof(Transaction.NetworkWrapper)));

    [TestCaseSource(nameof(GetBlockBodyValues))]
    public void Should_not_contain_network_form_tx_wrapper(BlockBody[] bodies)
    {
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 16);
        BlockBodiesMessageSerializer serializer = new();
        serializer.Serialize(buffer, new BlockBodiesMessage { Bodies = bodies });
        BlockBodiesMessage deserializedMessage = serializer.Deserialize(buffer);
        foreach (BlockBody? body in deserializedMessage.Bodies)
        {
            if (body is null) continue;
            foreach (Transaction tx in body.Transactions.Where(t => t.SupportsBlobs))
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
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Sepolia, LimboLogs.Instance), TestItem.PrivateKeyA)
            .TestObject;

        tx.SenderAddress = null;

        // null body
        yield return new BlockBody[] { null };

        // body with null withdrawals
        yield return new BlockBody[] { new(new[] { tx }, Array.Empty<BlockHeader>(), null) };

        yield return new BlockBody[]
        {
            // body with emtpy withdrawals
            new(new[] { tx }, new[] { header }, Array.Empty<Withdrawal>()),
            // body with a single withdrawals
            new(new[] { tx }, Array.Empty<BlockHeader>(),
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
                        .SignedAndResolved(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance),
                            TestItem.PrivateKeyA)
                        .TestObject,
                    Build.A.Transaction
                        .WithChainId(TestBlockchainIds.ChainId)
                        .WithTo(TestItem.AddressA)
                        .WithShardBlobTxTypeAndFields(2)
                        .SignedAndResolved(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance),
                            TestItem.PrivateKeyA)
                        .TestObject,
                    Build.A.Transaction
                        .WithChainId(TestBlockchainIds.ChainId)
                        .WithTo(TestItem.AddressA)
                        .WithShardBlobTxTypeAndFields(3, false)
                        .SignedAndResolved(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance),
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
