// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
        new BlockBodiesMessage { Bodies = bodies });

    private static IEnumerable<BlockBody[]> GetBlockBodyValues()
    {
        var header = Build.A.BlockHeader.TestObject;
        var tx = Build.A.Transaction
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
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
            new(new[] { tx }, new[] { header },
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
                })
        };
    }
}
