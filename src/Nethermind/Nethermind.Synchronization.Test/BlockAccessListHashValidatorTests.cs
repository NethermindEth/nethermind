// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public class BlockAccessListHashValidatorTests
{
    [Test]
    public void Accepts_the_canonical_encoding_of_a_list_matching_the_header()
    {
        byte[] encoded = BlockAccessListEncodings.Canonical;
        ReadOnlyBlockAccessList list = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges
                .WithAddress(new Address("0x0000000000000000000000000000000000000001"))
                .WithBalanceChanges([new(0, 0x80)])
                .WithNonceChanges([new(1, 5)])
                .TestObject)
            .TestObject;

        bool isValid = BlockAccessListHashValidator.Validate(HeaderCommittingTo(encoded), encoded, out string? errorMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(Rlp.Encode(list)));
            Assert.That(isValid, Is.True);
            Assert.That(errorMessage, Is.Null);
        }
    }

    // Each list matches the header's commitment, so only the encoding checks of engine_newPayload can reject it.
    [TestCaseSource(nameof(EncodingsRejectedByNewPayload))]
    public void Rejects_a_list_matching_the_header_in_an_encoding_newPayload_rejects(byte[] encoded)
    {
        bool isValid = BlockAccessListHashValidator.Validate(HeaderCommittingTo(encoded), encoded, out string? errorMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isValid, Is.False);
            Assert.That(errorMessage, Does.StartWith("error decoding block access list"));
        }
    }

    [Test]
    public void Reports_a_list_not_matching_the_header_as_a_hash_mismatch()
    {
        // Not a block access list either, but the hash is checked first.
        byte[] encoded = [0xde, 0xad];

        bool isValid = BlockAccessListHashValidator.Validate(HeaderCommittingTo(BlockAccessListEncodings.Canonical), encoded, out string? errorMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isValid, Is.False);
            Assert.That(errorMessage, Does.StartWith("block access list hash mismatch"));
        }
    }

    [Test]
    public void Rejects_a_list_for_a_header_without_a_block_access_list_hash()
    {
        BlockHeader header = Build.A.BlockHeader.WithBlockAccessListHash(null).TestObject;

        bool isValid = BlockAccessListHashValidator.Validate(header, BlockAccessListEncodings.Canonical, out string? errorMessage);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(isValid, Is.False);
            Assert.That(errorMessage, Is.EqualTo("block header is missing block access list hash"));
        }
    }

    private static BlockHeader HeaderCommittingTo(byte[] encoded) =>
        Build.A.BlockHeader.WithBlockAccessListHash(Keccak.Compute(encoded)).TestObject;

    private static IEnumerable<TestCaseData> EncodingsRejectedByNewPayload()
    {
        yield return new TestCaseData(BlockAccessListEncodings.LeadingZero) { TestName = "integer_with_leading_zero" };
        // The zero index of the balance change written as 0x00 instead of 0x80.
        yield return new TestCaseData(Bytes.FromHexString("0xe2e1940000000000000000000000000000000000000001c0c0c4c3008180c3c20105c0")) { TestName = "zero_written_as_0x00" };
        // The outer list with a long-form length below 56.
        yield return new TestCaseData(Bytes.FromHexString("0xf822e1940000000000000000000000000000000000000001c0c0c4c3808180c3c20105c0")) { TestName = "short_list_with_long_form_length" };
        yield return new TestCaseData(Bytes.FromHexString("0xe2e1940000000000000000000000000000000000000001c0c0c4c3808180c3c20105c000")) { TestName = "trailing_byte" };
        // Two account reads, 0x00..02 before 0x00..01.
        yield return new TestCaseData(Bytes.FromHexString("0xf6da940000000000000000000000000000000000000002c0c0c0c0c0da940000000000000000000000000000000000000001c0c0c0c0c0")) { TestName = "accounts_out_of_order" };
        yield return new TestCaseData(Bytes.FromHexString("0xc1c0")) { TestName = "empty_account_entry" };
        // A balance of 33 bytes, over the 32 bytes of an integer, which the reader reports as a limit error.
        yield return new TestCaseData(Bytes.FromHexString("0xf843f841940000000000000000000000000000000000000001c0c0e4e380a1010101010101010101010101010101010101010101010101010101010101010101c3c20105c0")) { TestName = "value_longer_than_32_bytes" };
    }
}
