// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using static Nethermind.LightClient.Test.ExecutionProofFixtures;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class SnapProofTests
{
    private static readonly Address Address = new("0x1234567890123456789012345678901234567890");

    [Test]
    public void Account_range_is_authenticated_against_finalized_root([Values] bool tamper)
    {
        Account account = new(7, 123);
        ValueHash256 path = ValueKeccak.Compute(Address.Bytes);
        byte[] leaf = Leaf(path.ToCommitment(), AccountDecoder.Instance.EncodeAsBytes(account));
        using AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(path, tamper ? new Account(7, 124) : account) },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };

        if (tamper)
            Assert.That(() => ExecutionPeerTransport.VerifyAccountRange(Keccak.Compute(leaf), Address, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(ExecutionPeerTransport.VerifyAccountRange(Keccak.Compute(leaf), Address, response, NullLogManager.Instance), Is.EqualTo(account));
    }

    [Test]
    public void Empty_account_range_needs_a_valid_absence_proof([Values] bool includeProof)
    {
        byte[] leaf = Leaf(Keccak.Compute(Address.Bytes), AccountDecoder.Instance.EncodeAsBytes(new Account(123)));
        ArrayPoolList<byte[]> proofs = new(1);
        if (includeProof) proofs.Add(leaf);
        using AccountsAndProofs response = new()
        {
            Proofs = new ByteArrayListAdapter(proofs),
        };
        if (includeProof)
            Assert.That(ExecutionPeerTransport.VerifyAccountRange(Keccak.Compute(leaf), Address.Zero, response, NullLogManager.Instance), Is.EqualTo(Account.TotallyEmpty));
        else
            Assert.That(() => ExecutionPeerTransport.VerifyAccountRange(Keccak.Compute(leaf), Address.Zero, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Storage_range_is_authenticated_against_account_root([Values] bool tamper)
    {
        UInt256 key = 1;
        ValueHash256 path = ValueKeccak.Compute(key.ToBigEndian());
        byte[] slotRlp = Rlp.Encode((UInt256)42).Bytes;
        byte[] leaf = Leaf(path.ToCommitment(), slotRlp);
        Account account = new(7, 123, Keccak.Compute(leaf), Keccak.OfAnEmptyString);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            {
                new ArrayPoolList<PathWithStorageSlot>(1) { new(path, tamper ? Rlp.Encode((UInt256)43).Bytes : slotRlp) }
            },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };

        if (tamper)
            Assert.That(() => ExecutionPeerTransport.VerifyStorageRange(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(ExecutionPeerTransport.VerifyStorageRange(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Is.EqualTo((UInt256)42));
    }
}
