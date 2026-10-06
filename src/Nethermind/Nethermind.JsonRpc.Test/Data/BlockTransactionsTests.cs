// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data;

/// <summary>Clients that read a block back need the transactions in the form the server sent, typed rather than as raw JSON.</summary>
public class BlockTransactionsTests
{
    [TestCase("cancun-full")]
    [TestCase("cancun-hashes")]
    [TestCase("pre-merge-full")]
    [TestCase("pre-merge-hashes")]
    [TestCase("empty-full")]
    [TestCase("empty-hashes")]
    public void Block_reads_back_and_writes_the_same_bytes(string fixture)
    {
        string json = JsonFixture.Read(typeof(BlockTransactionsTests).Assembly, fixture);

        BlockForRpc block = TypeInfoJsonSerializer.Deserialize<BlockForRpc>(json, EthereumJsonSerializer.JsonOptions)!;

        Assert.That(BlockForRpcWireFormatTests.Serialize(block), Is.EqualTo(json));
    }

    [Test]
    public void Full_transactions_read_back_as_their_rpc_types()
    {
        BlockForRpc block = Read("cancun-full");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions!.Hashes, Is.Null);
            Assert.That(block.Transactions.Full!.Select(static t => t.Type), Is.EqualTo(BlockForRpcWireFormatTests.AllTypes().Select(static t => (TxType?)t.Type)));
            Assert.That(block.Transactions.Full!.Select(static t => t.Hash), Is.EqualTo(BlockForRpcWireFormatTests.AllTypes().Select(static t => t.Hash)));
        }
    }

    [Test]
    public void Hashes_read_back_as_hashes()
    {
        BlockForRpc block = Read("cancun-hashes");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions!.Full, Is.Null);
            Assert.That(block.Transactions.Hashes, Is.EqualTo(BlockForRpcWireFormatTests.AllTypes().Select(static t => t.Hash)));
        }
    }

    [Test]
    public void Empty_array_reads_back_as_both_forms()
    {
        BlockForRpc block = Read("empty-full");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions!.Full, Is.Empty);
            Assert.That(block.Transactions.Hashes, Is.Empty);
        }
    }

    [TestCase("""["0x0000000000000000000000000000000000000000000000000000000000000001",{}]""")]
    [TestCase("""[{"type":"0x2"},"0x0000000000000000000000000000000000000000000000000000000000000001"]""")]
    [TestCase("""[null]""")]
    [TestCase("""{}""")]
    public void Malformed_transactions_are_rejected(string transactions)
    {
        string json = $$"""{"transactions":{{transactions}}}""";
        Assert.That(() => TypeInfoJsonSerializer.Deserialize<BlockForRpc>(json, EthereumJsonSerializer.JsonOptions), Throws.InstanceOf<JsonException>());
    }

    private static BlockForRpc Read(string fixture) =>
        TypeInfoJsonSerializer.Deserialize<BlockForRpc>(JsonFixture.Read(typeof(BlockTransactionsTests).Assembly, fixture), EthereumJsonSerializer.JsonOptions)
        ?? throw new InvalidOperationException();
}
