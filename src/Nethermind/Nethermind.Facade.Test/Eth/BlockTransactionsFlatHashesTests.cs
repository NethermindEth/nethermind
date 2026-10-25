// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Eth;

public class BlockTransactionsFlatHashesTests
{
    private static readonly ValueHash256[] FlatHashes = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];
    private static readonly Hash256[] HashObjects = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];

    [Test]
    public void FromHashes_Serialized_WritesWhatHashObjectsWrite() =>
        Assert.That(Serialize(BlockTransactions.FromHashes(FlatHashes), EthereumJsonSerializer.JsonOptions),
            Is.EqualTo(Serialize(HashObjects, EthereumJsonSerializer.JsonOptions)),
            "flat hashes must be written exactly as Hash256 objects are");

    [Test]
    public void FromHashes_WithAnotherHashConverter_WritesThroughThatConverter()
    {
        JsonSerializerOptions options = WithUpperCaseHashes();
        string hashObjects = Serialize(HashObjects, options);
        Assert.That(hashObjects, Is.Not.EqualTo(Serialize(HashObjects, EthereumJsonSerializer.JsonOptions)),
            "precondition: the replacement converter is the one in use");

        Assert.That(Serialize(BlockTransactions.FromHashes(FlatHashes), options), Is.EqualTo(hashObjects),
            "flat hashes must follow the configured converter");
    }

    [Test]
    public void FromHashes_Hashes_ReturnsTheHashesAsObjects()
    {
        BlockTransactions transactions = BlockTransactions.FromHashes(FlatHashes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactions.Hashes, Is.EqualTo(HashObjects), "the hashes are readable as Hash256 objects");
            Assert.That(transactions.Full, Is.Null, "hashes carry no full transactions");
        }
    }

    [Test]
    public void FromHashes_EmptyArray_ReturnsTheSharedEmptyInstance() =>
        Assert.That(BlockTransactions.FromHashes([]), Is.SameAs((BlockTransactions)Array.Empty<Hash256>()),
            "an empty block shares the empty instance, as hash objects do");

    private static string Serialize(BlockTransactions transactions, JsonSerializerOptions options) =>
        TypeInfoJsonSerializer.Serialize(transactions, options);

    private static JsonSerializerOptions WithUpperCaseHashes()
    {
        JsonSerializerOptions options = new(EthereumJsonSerializer.JsonOptions);
        for (int i = options.Converters.Count - 1; i >= 0; i--)
        {
            if (options.Converters[i] is Hash256Converter) options.Converters.RemoveAt(i);
        }

        options.Converters.Insert(0, new UpperCaseHash256Converter());
        return options;
    }

    private sealed class UpperCaseHash256Converter : Hash256Converter
    {
        public override void Write(Utf8JsonWriter writer, Hash256? keccak, JsonSerializerOptions options) =>
            writer.WriteStringValue(keccak!.ToString().ToUpperInvariant());
    }
}
