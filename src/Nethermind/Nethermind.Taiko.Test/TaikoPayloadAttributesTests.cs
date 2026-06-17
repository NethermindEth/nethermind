// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Taiko.Test;

/// <summary>
/// Verifies <see cref="TaikoPayloadAttributes.GetPayloadId"/> reproduces alethia-reth's
/// <c>payload_id_taiko</c> (SHA-256 over the Taiko field set with a V2 version tag) rather than
/// the base Keccak id, so the value matches the <c>buildPayloadArgsId</c> the driver expects.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class TaikoPayloadAttributesTests
{
    private const byte VersionV2 = 0x02;

    private static BlockHeader Parent() => Build.A.BlockHeader.TestObject;

    private static TaikoPayloadAttributes BuildAttributes(byte[]? extraData = null, Withdrawal[]? withdrawals = null) => new()
    {
        Timestamp = 1_700_000_000,
        PrevRandao = TestItem.KeccakB,
        SuggestedFeeRecipient = TestItem.AddressA,
        Withdrawals = withdrawals ?? [],
        BlockMetadata = new BlockMetadata
        {
            Beneficiary = TestItem.AddressA,
            GasLimit = 16_000_000,
            Timestamp = 1_700_000_000,
            MixHash = TestItem.KeccakB,
            TxList = [],
            ExtraData = extraData ?? [0x4b, 0x00],
        },
    };

    /// <summary>Reference implementation mirroring alethia-reth <c>payload_id_taiko</c>.</summary>
    private static string ExpectedId(BlockHeader parent, TaikoPayloadAttributes attrs)
    {
        List<byte> input = [.. parent.Hash!.Bytes];

        Span<byte> timestamp = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, attrs.Timestamp);
        input.AddRange(timestamp);

        input.AddRange((attrs.PrevRandao ?? Keccak.Zero).Bytes);
        input.AddRange((attrs.SuggestedFeeRecipient ?? Address.Zero).Bytes);
        // Empty withdrawals list RLP-encodes to a single 0xc0 byte.
        if (attrs.Withdrawals is { Length: 0 }) input.Add(0xc0);
        if (attrs.ParentBeaconBlockRoot is not null) input.AddRange(attrs.ParentBeaconBlockRoot.Bytes);
        input.AddRange(Keccak.Compute(attrs.BlockMetadata!.TxList).Bytes);
        input.AddRange(attrs.BlockMetadata.ExtraData);

        byte[] digest = SHA256.HashData(input.ToArray());
        digest[0] = VersionV2;
        return digest.AsSpan(0, 8).ToHexString(true);
    }

    [Test]
    public void GetPayloadId_matches_independent_sha256_reference()
    {
        BlockHeader parent = Parent();
        TaikoPayloadAttributes attrs = BuildAttributes();

        Assert.That(attrs.GetPayloadId(parent), Is.EqualTo(ExpectedId(parent, attrs)));
    }

    [Test]
    public void GetPayloadId_first_byte_is_v2_version_tag()
    {
        string id = BuildAttributes().GetPayloadId(Parent());
        Assert.That(id, Does.StartWith("0x02"));
    }

    [Test]
    public void GetPayloadId_differs_from_base_keccak_id()
    {
        BlockHeader parent = Parent();
        TaikoPayloadAttributes taiko = BuildAttributes();
        // A base PayloadAttributes with the same shared fields keccak-hashes a different field
        // set, so the two ids must not coincide.
        PayloadAttributes baseAttrs = new()
        {
            Timestamp = taiko.Timestamp,
            PrevRandao = taiko.PrevRandao,
            SuggestedFeeRecipient = taiko.SuggestedFeeRecipient,
            Withdrawals = taiko.Withdrawals,
        };

        Assert.That(taiko.GetPayloadId(parent), Is.Not.EqualTo(baseAttrs.GetPayloadId(parent)));
    }

    [Test]
    public void GetPayloadId_changes_with_extra_data()
    {
        BlockHeader parent = Parent();
        string first = BuildAttributes(extraData: [0x01]).GetPayloadId(parent);
        string second = BuildAttributes(extraData: [0x02]).GetPayloadId(parent);

        Assert.That(first, Is.Not.EqualTo(second));
    }

    [Test]
    public void GetPayloadId_changes_with_withdrawals()
    {
        BlockHeader parent = Parent();
        string empty = BuildAttributes().GetPayloadId(parent);
        string withOne = BuildAttributes(withdrawals: [new Withdrawal { Index = 1, ValidatorIndex = 2, Address = TestItem.AddressB, AmountInGwei = 3 }])
            .GetPayloadId(parent);

        Assert.That(empty, Is.Not.EqualTo(withOne));
    }

    [Test]
    public void GetPayloadId_matches_reference_with_non_empty_withdrawals()
    {
        BlockHeader parent = Parent();
        TaikoPayloadAttributes attrs = BuildAttributes(
            withdrawals: [new Withdrawal { Index = 1, ValidatorIndex = 2, Address = Address.Zero, AmountInGwei = 3 }]);

        // Hand-encoded EIP-4895 RLP of the single withdrawal, independent of the production
        // encoder: outer list (0xd9) -> withdrawal seq (0xd8) -> index, validatorIndex,
        // 20-byte zero address (0x94 prefix), amount.
        byte[] withdrawalsRlp = [0xd9, 0xd8, 0x01, 0x02, 0x94, .. new byte[20], 0x03];

        List<byte> input = [.. parent.Hash!.Bytes];
        Span<byte> timestamp = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, attrs.Timestamp);
        input.AddRange(timestamp);
        input.AddRange((attrs.PrevRandao ?? Keccak.Zero).Bytes);
        input.AddRange((attrs.SuggestedFeeRecipient ?? Address.Zero).Bytes);
        input.AddRange(withdrawalsRlp);
        input.AddRange(Keccak.Compute(attrs.BlockMetadata!.TxList).Bytes);
        input.AddRange(attrs.BlockMetadata.ExtraData);

        byte[] digest = SHA256.HashData(input.ToArray());
        digest[0] = VersionV2;
        Assert.That(attrs.GetPayloadId(parent), Is.EqualTo(digest.AsSpan(0, 8).ToHexString(true)));
    }

    [Test]
    public void GetPayloadId_matches_reference_with_parent_beacon_block_root()
    {
        BlockHeader parent = Parent();
        TaikoPayloadAttributes attrs = BuildAttributes();
        attrs.ParentBeaconBlockRoot = TestItem.KeccakC;

        Assert.That(attrs.GetPayloadId(parent), Is.EqualTo(ExpectedId(parent, attrs)));
    }
}
