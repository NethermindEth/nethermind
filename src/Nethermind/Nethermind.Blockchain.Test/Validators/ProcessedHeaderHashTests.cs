// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Validators;

[Parallelizable(ParallelScope.All)]
public class ProcessedHeaderHashTests
{
    private static readonly BlockValidator Validator = new(
        Always.Valid, Always.Valid, Always.Valid, Substitute.For<ISpecProvider>(), LimboLogs.Instance);

    private static IEnumerable<string> WritableMembers() =>
        typeof(BlockHeader).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static p => p.SetMethod?.IsPublic == true)
            .Select(static p => p.Name)
            .Concat(typeof(BlockHeader).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(static f => f.Name))
            .Where(static name => name != nameof(BlockHeader.Hash));

    [Test]
    public void Unchanged_header_takes_the_suggested_hash([Values] bool allFields)
    {
        BlockHeader suggested = CreateSuggestedHeader(allFields);

        Assert.That(ProcessedHeaderHash.Calculate(CreateSuggestedHeader(allFields), suggested), Is.SameAs(suggested.Hash));
    }

    /// <remarks>
    /// Covers every writable member, so a field that the header encoding gains but the comparison misses makes the
    /// processed hash differ from the calculated one, and a member of a new type fails in <see cref="Change"/>.
    /// </remarks>
    [Test]
    public void Changed_header_gets_its_calculated_hash(
        [ValueSource(nameof(WritableMembers))] string member, [Values] bool allFields)
    {
        BlockHeader suggested = CreateSuggestedHeader(allFields);
        BlockHeader processed = CreateSuggestedHeader(allFields);
        Change(processed, member);

        Hash256 calculated = processed.CalculateHash();
        processed.Hash = ProcessedHeaderHash.Calculate(processed, suggested);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processed.Hash, Is.EqualTo(calculated));
            Assert.That(Validator.ValidateProcessedBlock(new Block(processed), [], new Block(suggested)),
                Is.EqualTo(calculated == suggested.Hash));
        }
    }

    [Test]
    public void Processed_block_with_a_wrong_consensus_field_is_rejected(
        [Values(nameof(BlockHeader.StateRoot), nameof(BlockHeader.ReceiptsRoot), nameof(BlockHeader.Bloom),
            nameof(BlockHeader.GasUsed), nameof(BlockHeader.BlobGasUsed), nameof(BlockHeader.ExcessBlobGas),
            nameof(BlockHeader.WithdrawalsRoot), nameof(BlockHeader.ParentBeaconBlockRoot), nameof(BlockHeader.RequestsHash),
            nameof(BlockHeader.BlockAccessListHash), nameof(BlockHeader.SlotNumber), nameof(BlockHeader.BaseFeePerGas),
            nameof(BlockHeader.ExtraData))]
        string member)
    {
        BlockHeader suggested = CreateSuggestedHeader(allFields: true);
        BlockHeader processed = CreateSuggestedHeader(allFields: true);
        Change(processed, member);
        processed.Hash = ProcessedHeaderHash.Calculate(processed, suggested);

        Assert.That(Validator.ValidateProcessedBlock(new Block(processed), [], new Block(suggested)), Is.False);
    }

    private static BlockHeader CreateSuggestedHeader(bool allFields)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithParentHash(TestItem.KeccakA)
            .WithBeneficiary(TestItem.AddressA)
            .WithStateRoot(TestItem.KeccakB)
            .WithTransactionsRoot(TestItem.KeccakC)
            .WithReceiptsRoot(TestItem.KeccakD)
            .WithBloom(new Bloom(Enumerable.Repeat((byte)0x0f, Bloom.ByteLength).ToArray()))
            .WithNumber(100)
            .WithGasUsed(21_000)
            .WithTimestamp(1_000)
            .WithExtraData([1, 2, 3])
            .WithMixHash(TestItem.KeccakE)
            .WithNonce(7)
            .TestObject;

        if (allFields)
        {
            header.BaseFeePerGas = 10;
            header.WithdrawalsRoot = TestItem.KeccakF;
            header.BlobGasUsed = 131_072;
            header.ExcessBlobGas = 262_144;
            header.ParentBeaconBlockRoot = TestItem.KeccakG;
            header.RequestsHash = TestItem.KeccakH;
            header.BlockAccessListHash = TestItem.KeccakA;
            header.SlotNumber = 5;
        }

        header.Hash = header.CalculateHash();
        return header;
    }

    private static void Change(BlockHeader header, string member)
    {
        if (typeof(BlockHeader).GetProperty(member) is { } property)
            property.SetValue(header, Changed(property.GetValue(header), property.PropertyType, member));
        else
        {
            FieldInfo field = typeof(BlockHeader).GetField(member)!;
            field.SetValue(header, Changed(field.GetValue(header), field.FieldType, member));
        }
    }

    private static object Changed(object? value, Type type, string member) => value switch
    {
        Hash256 hash => Keccak.Compute(hash.Bytes),
        Address address => address == TestItem.AddressB ? TestItem.AddressC : TestItem.AddressB,
        Bloom bloom => new Bloom(bloom.Bytes.ToArray().Select(static b => (byte)~b).ToArray()),
        byte[] bytes => (byte[])[.. bytes, 0xff],
        UInt256 number => number + 1,
        ulong number => number + 1,
        bool flag => !flag,
        null when type == typeof(Hash256) => TestItem.KeccakB,
        null when type == typeof(Address) => TestItem.AddressB,
        null when type == typeof(Bloom) => new Bloom(Enumerable.Repeat((byte)0xf0, Bloom.ByteLength).ToArray()),
        null when type == typeof(ulong?) => 1UL,
        null when type == typeof(UInt256?) => UInt256.One,
        null when type == typeof((ulong, ulong)?) => (1UL, 2UL),
        _ => throw new NotSupportedException($"Add a change for {nameof(BlockHeader)}.{member} of type {type}.")
    };
}
