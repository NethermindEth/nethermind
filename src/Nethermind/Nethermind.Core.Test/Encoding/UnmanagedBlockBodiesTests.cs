// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Buffers;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding;

public class UnmanagedBlockBodiesTests
{
    [TestCaseSource(nameof(GetBlockBodyValues))]
    public void Should_pass_roundtrip(BlockBody?[] bodies)
    {
        BlockBodiesMessageSerializer serializer = new BlockBodiesMessageSerializer();
        IByteBuffer buffer = Unpooled.Buffer(1);

        serializer.Serialize(buffer, new BlockBodiesMessage(bodies));

        NettyBufferMemoryOwner memoryOwner = new NettyBufferMemoryOwner(buffer);
        UnmanagedBlockBodies unmanagedBlockBodies = new UnmanagedBlockBodies(memoryOwner.Memory);

        BlockBody?[] bodyBack = unmanagedBlockBodies.DeserializeBodies();
        bodyBack.Should().BeEquivalentTo(bodies, o =>
            o.Excluding(c => c.Name == nameof(Transaction.SenderAddress))
                .Excluding(c => c.Name == nameof(Transaction.NetworkWrapper)));
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
