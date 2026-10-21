// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using static Nethermind.LightClient.Test.ExecutionProofFixtures;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class ExecutionProofVerifierTests
{
    private static readonly Address AccountAddress = new("0x1234567890123456789012345678901234567890");

    [Test]
    public void Account_values_come_from_the_requested_authenticated_leaf()
    {
        Account expected = new(7, 123, Keccak.Compute([1, 2]), Keccak.Compute([3, 4]));
        byte[] leaf = Leaf(Keccak.Compute(AccountAddress.Bytes), AccountDecoder.Instance.EncodeAsBytes(expected));

        Account actual = ExecutionProofVerifier.VerifyAccount(Keccak.Compute(leaf), AccountAddress, [leaf]);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void A_different_account_leaf_proves_absence()
    {
        byte[] leaf = Leaf(Keccak.Compute(AccountAddress.Bytes), AccountDecoder.Instance.EncodeAsBytes(new Account(123)));

        Account actual = ExecutionProofVerifier.VerifyAccount(Keccak.Compute(leaf), Address.Zero, [leaf]);

        Assert.That(actual, Is.EqualTo(Account.TotallyEmpty));
    }

    [Test]
    public void An_empty_trie_proves_absence_without_nodes()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExecutionProofVerifier.VerifyAccount(Keccak.EmptyTreeHash, AccountAddress, []), Is.EqualTo(Account.TotallyEmpty));
            Assert.That(ExecutionProofVerifier.VerifyStorage(Keccak.EmptyTreeHash, 1, []), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Missing_or_tampered_root_is_rejected([Values] bool tamper)
    {
        byte[] leaf = Leaf(Keccak.Compute(AccountAddress.Bytes), AccountDecoder.Instance.EncodeAsBytes(new Account(123)));
        Hash256 root = Keccak.Compute(leaf);
        byte[][] proof = tamper ? [leaf] : [];
        if (tamper) leaf[^1] ^= 1;

        Assert.That(() => ExecutionProofVerifier.VerifyAccount(root, AccountAddress, proof), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Missing_selected_child_is_not_absence([Values] bool includeChild)
    {
        Hash256 key = Keccak.Compute(AccountAddress.Bytes);
        byte[] leaf = Rlp.Encode(Rlp.Encode(OddPath(key.Bytes)), Rlp.Encode(AccountDecoder.Instance.EncodeAsBytes(new Account(123)))).Bytes;
        byte[] branch = CreateBranch(key.Bytes[0] >> 4, Rlp.Encode(Keccak.Compute(leaf)));

        if (includeChild)
        {
            Account actual = ExecutionProofVerifier.VerifyAccount(Keccak.Compute(branch), AccountAddress, [leaf, branch]);
            Assert.That(actual.Balance, Is.EqualTo((UInt256)123));
        }
        else
        {
            Assert.That(() => ExecutionProofVerifier.VerifyAccount(Keccak.Compute(branch), AccountAddress, [branch]), Throws.TypeOf<InvalidDataException>());
        }
    }

    [Test]
    public void An_empty_selected_branch_child_proves_absence()
    {
        Hash256 key = Keccak.Compute(AccountAddress.Bytes);
        byte[] branch = CreateBranch((key.Bytes[0] >> 4) ^ 1, Rlp.Encode(Keccak.Compute([1, 2, 3])));

        Assert.That(ExecutionProofVerifier.VerifyAccount(Keccak.Compute(branch), AccountAddress, [branch]), Is.EqualTo(Account.TotallyEmpty));
    }

    [Test]
    public void Storage_uses_the_padded_slot_hash([Values(1UL, 255UL, 256UL)] ulong slot)
    {
        UInt256 expected = 987;
        byte[] leaf = Leaf(StorageKey(slot), Rlp.Encode(expected).Bytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ExecutionProofVerifier.VerifyStorage(Keccak.Compute(leaf), slot, [leaf]), Is.EqualTo(expected));
            Assert.That(ExecutionProofVerifier.VerifyStorage(Keccak.Compute(leaf), slot + 1, [leaf]), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Storage_accepts_embedded_children_and_extension_nodes()
    {
        Hash256 key = StorageKey(1);
        Rlp leaf = Rlp.Encode(Rlp.Encode(new byte[] { (byte)(0x30 | (key.Bytes[31] & 15)) }), Rlp.Encode(Rlp.Encode((UInt256)42).Bytes));
        byte[] branch = CreateBranch(key.Bytes[31] >> 4, leaf);
        byte[] path = new byte[32];
        key.Bytes[..31].CopyTo(path.AsSpan(1));
        byte[] extension = Rlp.Encode(Rlp.Encode(path), Rlp.Encode(Keccak.Compute(branch))).Bytes;

        Assert.That(ExecutionProofVerifier.VerifyStorage(Keccak.Compute(extension), 1, [extension, branch]), Is.EqualTo((UInt256)42));
    }

    [Test]
    public void Noncanonical_storage_scalar_is_rejected([Values(0, 1, 2)] int invalidEncoding)
    {
        byte[] value = invalidEncoding switch
        {
            0 => [0x81, 1],
            1 => [1, 2],
            _ => [0x80]
        };
        byte[] leaf = Leaf(StorageKey(1), value);

        Assert.That(() => ExecutionProofVerifier.VerifyStorage(Keccak.Compute(leaf), 1, [leaf]), Throws.Exception);
    }

    [Test]
    public void Account_rlp_with_trailing_bytes_is_rejected()
    {
        byte[] account = AccountDecoder.Instance.EncodeAsBytes(new Account(123));
        byte[] value = new byte[account.Length + 1];
        account.CopyTo(value, 0);
        byte[] leaf = Leaf(Keccak.Compute(AccountAddress.Bytes), value);

        Assert.That(() => ExecutionProofVerifier.VerifyAccount(Keccak.Compute(leaf), AccountAddress, [leaf]), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Oversized_proof_node_is_rejected([Values(0, 1025)] int length)
        => Assert.That(() => ExecutionProofVerifier.VerifyStorage(Keccak.EmptyTreeHash, 1, [new byte[length]]), Throws.TypeOf<InvalidDataException>());

    [Test]
    public void Too_many_proof_nodes_are_rejected()
        => Assert.That(() => ExecutionProofVerifier.VerifyStorage(Keccak.EmptyTreeHash, 1, new byte[66][]), Throws.TypeOf<InvalidDataException>());

    [Test]
    public void Code_must_match_the_authenticated_hash([Values] bool tamper)
    {
        byte[] code = [0x60, 0x01, 0x00];
        Hash256 hash = Keccak.Compute(code);
        if (tamper) code[1] ^= 1;

        if (tamper)
            Assert.That(() => ExecutionProofVerifier.VerifyCode(hash, code), Throws.TypeOf<InvalidDataException>());
        else
            Assert.That(() => ExecutionProofVerifier.VerifyCode(hash, code), Throws.Nothing);
    }

    private static Hash256 StorageKey(UInt256 slot)
    {
        Span<byte> bytes = stackalloc byte[32];
        slot.ToBigEndian(bytes);
        return Keccak.Compute(bytes);
    }

    private static byte[] OddPath(ReadOnlySpan<byte> key)
    {
        byte[] path = key.ToArray();
        path[0] = (byte)(0x30 | (key[0] & 15));
        return path;
    }

    private static byte[] CreateBranch(int nibble, Rlp child)
    {
        Rlp[] children = new Rlp[17];
        Array.Fill(children, Rlp.OfEmptyByteArray);
        children[nibble] = child;
        return Rlp.Encode(children).Bytes;
    }
}

internal static class ExecutionProofFixtures
{
    internal static byte[] Leaf(Hash256 key, byte[] value)
    {
        byte[] path = new byte[33];
        path[0] = 0x20;
        key.Bytes.CopyTo(path.AsSpan(1));
        return Rlp.Encode(Rlp.Encode(path), Rlp.Encode(value)).Bytes;
    }
}
