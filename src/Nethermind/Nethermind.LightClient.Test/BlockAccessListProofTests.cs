// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class BlockAccessListProofTests
{
    [Test]
    public void Empty_BAL_is_valid_and_distinct_from_unavailable_data()
    {
        byte[] encoded = [0xc0];
        BlockHeader header = Header(encoded);
        ReadOnlyBlockAccessList decoded = ExecutionPeerTransport.VerifyBlockAccessList(header, encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.AccountChanges.Count, Is.Zero);
            Assert.That(decoded.WireHash, Is.EqualTo(header.BlockAccessListHash));
            Assert.That(() => ExecutionPeerTransport.VerifyBlockAccessList(header, []), Throws.TypeOf<InvalidDataException>());
        }
    }

    [Test]
    public void BAL_item_count_is_bounded_by_authenticated_gas_limit([Values(3999UL, 4000UL, 4001UL)] ulong gasLimit)
    {
        ReadOnlyBlockAccessList list = new([new(Address.Zero, [new(1, [new(1, 42)])], [], [], [], [])], 2);
        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(list);
        BlockHeader header = Header(encoded, gasLimit);
        if (gasLimit < 4000)
            Assert.That(() => ExecutionPeerTransport.VerifyBlockAccessList(header, encoded), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(ExecutionPeerTransport.VerifyBlockAccessList(header, encoded).ItemCount, Is.EqualTo(2));
    }

    [Test]
    public void Uncommitted_BAL_is_rejected_before_decoding([Values] bool missingCommitment)
    {
        BlockHeader header = Header([0xc0]);
        if (missingCommitment) header.BlockAccessListHash = null;
        Assert.That(() => ExecutionPeerTransport.VerifyBlockAccessList(header, [0xc1, 0x80]), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Committed_malformed_or_trailing_RLP_is_rejected([Values("c080", "c180", "80")] string hex)
    {
        byte[] encoded = Convert.FromHexString(hex);
        BlockHeader header = Header(encoded);
        Assert.That(() => ExecutionPeerTransport.VerifyBlockAccessList(header, encoded), Throws.Exception);
    }

    [Test]
    public void Nonordered_accounts_are_rejected_even_with_matching_commitment()
    {
        Address other = new("0xde198901C5ee4611142E0c61DfbbCEc7ab468F32");
        ReadOnlyBlockAccessList list = new([new(other, [], [], [], [], []), new(Address.Zero, [], [], [], [], [])], 2);
        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(list);
        BlockHeader header = Header(encoded);
        Assert.That(() => ExecutionPeerTransport.VerifyBlockAccessList(header, encoded), Throws.Exception);
    }

    [Test]
    public void Decoded_BAL_owns_its_storage_values_after_response_reuse()
    {
        ReadOnlyBlockAccessList list = new([new(Address.Zero, [new(1, [new(1, 42)])], [], [], [], [])], 2);
        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(list);
        ReadOnlyBlockAccessList decoded = ExecutionPeerTransport.VerifyBlockAccessList(Header(encoded), encoded);
        Array.Clear(encoded);
        Assert.That(decoded.GetAccountChanges(Address.Zero)!.StorageChanges[0].Changes[^1].Value, Is.EqualTo((UInt256)42));
    }

    private static BlockHeader Header(byte[] encoded, ulong gasLimit = 30_000_000) =>
        new(Hash256.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, UInt256.Zero, 1, gasLimit, 1_800_000_000, [])
        { BlockAccessListHash = Keccak.Compute(encoded) };
}
