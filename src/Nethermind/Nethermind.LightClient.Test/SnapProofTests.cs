// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        VerifiedHead head = new(0, 0, Hash256.Zero, Keccak.Compute(leaf));

        if (tamper)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => ExecutionPeerTransport.VerifyAccountRange(head.StateRoot, Address, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => ExecutionPeerTransport.ExportAccountProof(head, Address, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
            }
        }
        else
        {
            (Account exported, byte[][] witness) = ExecutionPeerTransport.ExportAccountProof(head, Address, response, NullLogManager.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exported, Is.EqualTo(account));
                Assert.That(ExecutionPeerTransport.VerifyAccountRange(head.StateRoot, Address, response, NullLogManager.Instance), Is.EqualTo(account));
                Assert.That(ExecutionProofVerifier.VerifyAccount(head.StateRoot, Address, witness), Is.EqualTo(account));
            }
        }
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
        VerifiedHead head = new(0, 0, Hash256.Zero, Keccak.Compute(leaf));
        if (includeProof)
        {
            (Account exported, byte[][] witness) = ExecutionPeerTransport.ExportAccountProof(head, Address.Zero, response, NullLogManager.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ExecutionPeerTransport.VerifyAccountRange(head.StateRoot, Address.Zero, response, NullLogManager.Instance), Is.EqualTo(Account.TotallyEmpty));
                Assert.That(exported, Is.EqualTo(Account.TotallyEmpty));
                Assert.That(ExecutionProofVerifier.VerifyAccount(head.StateRoot, Address.Zero, witness), Is.EqualTo(Account.TotallyEmpty));
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => ExecutionPeerTransport.VerifyAccountRange(head.StateRoot, Address.Zero, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => ExecutionPeerTransport.ExportAccountProof(head, Address.Zero, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
            }
        }
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
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => ExecutionPeerTransport.VerifyStorageRange(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
                Assert.That(() => ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
            }
        }
        else
        {
            ExecutionStorageProof exported = ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exported.Value, Is.EqualTo((UInt256)42));
                Assert.That(ExecutionPeerTransport.VerifyStorageRange(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Is.EqualTo((UInt256)42));
                Assert.That(ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, exported.Proof), Is.EqualTo((UInt256)42));
            }
        }
    }

    [Test]
    public void Storage_absence_is_verified_for_omitted_or_empty_group([Values] bool emptyGroup)
    {
        byte[] leaf = Leaf(ValueKeccak.Compute(((UInt256)1).ToBigEndian()).ToCommitment(), Rlp.Encode((UInt256)42).Bytes);
        Account account = new(7, 123, Keccak.Compute(leaf), Keccak.OfAnEmptyString);
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> groups = new(1);
        if (emptyGroup) groups.Add(new ArrayPoolList<PathWithStorageSlot>(0));
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = groups,
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };
        ExecutionStorageProof exported = ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), 2, response, NullLogManager.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exported.Value, Is.EqualTo(UInt256.Zero));
            Assert.That(ExecutionProofVerifier.VerifyStorage(account.StorageRoot, 2, exported.Proof), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void Account_export_includes_reconstructed_leaf_and_owns_its_witness([Values] bool truncateProof)
    {
        Account account = new(7, 123);
        ValueHash256 path = ValueKeccak.Compute(Address.Bytes);
        (byte[] branch, byte[] leaf) = BranchProof(path, AccountDecoder.Instance.EncodeAsBytes(account));
        VerifiedHead head = new(0, 0, Hash256.Zero, Keccak.Compute(branch));
        byte[][] expected = [branch.ToArray(), leaf.ToArray()];
        byte[][] witness;
        using (AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(path, account) },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { truncateProof ? branch[..^1] : branch }),
        })
        {
            if (truncateProof)
            {
                Assert.That(() => ExecutionPeerTransport.ExportAccountProof(head, Address, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
                return;
            }
            (Account exported, witness) = ExecutionPeerTransport.ExportAccountProof(head, Address, response, NullLogManager.Instance);
            Assert.That(exported, Is.EqualTo(account));
        }
        branch[0] ^= 1;
        leaf[0] ^= 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(witness, Is.EqualTo(expected));
            Assert.That(ExecutionProofVerifier.VerifyAccount(head.StateRoot, Address, witness), Is.EqualTo(account));
        }
    }

    [Test]
    public void Storage_export_includes_reconstructed_leaf_and_owns_its_witness([Values] bool truncateProof)
    {
        UInt256 key = 1;
        byte[] slotRlp = Rlp.Encode((UInt256)42).Bytes;
        ValueHash256 path = ValueKeccak.Compute(key.ToBigEndian());
        (byte[] branch, byte[] leaf) = BranchProof(path, slotRlp);
        Account account = new(7, 123, Keccak.Compute(branch), Keccak.OfAnEmptyString);
        byte[][] expected = [branch.ToArray(), leaf.ToArray()];
        ExecutionStorageProof exported;
        using (SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            {
                new ArrayPoolList<PathWithStorageSlot>(1) { new(path, slotRlp) }
            },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { truncateProof ? branch[..^1] : branch }),
        })
        {
            if (truncateProof)
            {
                Assert.That(() => ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
                return;
            }
            exported = ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance);
        }
        branch[0] ^= 1;
        leaf[0] ^= 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exported.Value, Is.EqualTo((UInt256)42));
            Assert.That(exported.Proof, Is.EqualTo(expected));
            Assert.That(ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, exported.Proof), Is.EqualTo((UInt256)42));
        }
    }

    [Test]
    public void Unordered_or_before_start_account_ranges_are_rejected([Values] bool duplicate)
    {
        ValueHash256 path = duplicate ? ValueKeccak.Compute(Address.Bytes) : ValueKeccak.Zero;
        Account account = new(7, 123);
        byte[] leaf = Leaf(path.ToCommitment(), AccountDecoder.Instance.EncodeAsBytes(account));
        ArrayPoolList<PathWithAccount> accounts = new(2) { new(path, account) };
        if (duplicate) accounts.Add(new(path, account));
        using AccountsAndProofs response = new()
        {
            PathAndAccounts = accounts,
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };
        VerifiedHead head = new(0, 0, Hash256.Zero, Keccak.Compute(leaf));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ExecutionPeerTransport.VerifyAccountRange(head.StateRoot, Address, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => ExecutionPeerTransport.ExportAccountProof(head, Address, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
        }
    }

    [Test]
    public void Unordered_or_before_start_storage_ranges_are_rejected([Values] bool duplicate)
    {
        UInt256 key = 1;
        ValueHash256 path = duplicate ? ValueKeccak.Compute(key.ToBigEndian()) : ValueKeccak.Zero;
        byte[] slotRlp = Rlp.Encode((UInt256)42).Bytes;
        byte[] leaf = Leaf(path.ToCommitment(), slotRlp);
        Account account = new(7, 123, Keccak.Compute(leaf), Keccak.OfAnEmptyString);
        ArrayPoolList<PathWithStorageSlot> slots = new(2) { new(path, slotRlp) };
        if (duplicate) slots.Add(new(path, slotRlp));
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { slots },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => ExecutionPeerTransport.VerifyStorageRange(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Throws.TypeOf<InvalidDataException>());
        }
    }

    [Test]
    public void A_returned_account_after_the_requested_limit_proves_absence()
    {
        ValueHash256 path = ValueKeccak.Compute(Address.Bytes).IncrementPath().IncrementPath();
        Account account = new(7, 123);
        byte[] leaf = Leaf(path.ToCommitment(), AccountDecoder.Instance.EncodeAsBytes(account));
        using AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(path, account) },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };
        VerifiedHead head = new(0, 0, Hash256.Zero, Keccak.Compute(leaf));

        (Account exported, byte[][] witness) = ExecutionPeerTransport.ExportAccountProof(head, Address, response, NullLogManager.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exported, Is.EqualTo(Account.TotallyEmpty));
            Assert.That(ExecutionPeerTransport.VerifyAccountRange(head.StateRoot, Address, response, NullLogManager.Instance), Is.EqualTo(Account.TotallyEmpty));
            Assert.That(ExecutionProofVerifier.VerifyAccount(head.StateRoot, Address, witness), Is.EqualTo(Account.TotallyEmpty));
        }
    }

    [Test]
    public void A_returned_slot_after_the_requested_limit_proves_absence()
    {
        UInt256 key = 1;
        ValueHash256 path = ValueKeccak.Compute(key.ToBigEndian()).IncrementPath().IncrementPath();
        byte[] slotRlp = Rlp.Encode((UInt256)42).Bytes;
        byte[] leaf = Leaf(path.ToCommitment(), slotRlp);
        Account account = new(7, 123, Keccak.Compute(leaf), Keccak.OfAnEmptyString);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
            {
                new ArrayPoolList<PathWithStorageSlot>(1) { new(path, slotRlp) }
            },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { leaf }),
        };

        ExecutionStorageProof exported = ExecutionPeerTransport.ExportStorageProof(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exported.Value, Is.EqualTo(UInt256.Zero));
            Assert.That(ExecutionPeerTransport.VerifyStorageRange(account, ValueKeccak.Compute(Address.Bytes), key, response, NullLogManager.Instance), Is.EqualTo(UInt256.Zero));
            Assert.That(ExecutionProofVerifier.VerifyStorage(account.StorageRoot, key, exported.Proof), Is.EqualTo(UInt256.Zero));
        }
    }

    private static (byte[] Branch, byte[] Leaf) BranchProof(ValueHash256 path, byte[] value)
    {
        byte[] compactPath = path.Bytes.ToArray();
        compactPath[0] = (byte)(0x30 | (compactPath[0] & 15));
        byte[] leaf = Rlp.Encode(Rlp.Encode(compactPath), Rlp.Encode(value)).Bytes;
        Rlp[] children = new Rlp[17];
        Array.Fill(children, Rlp.OfEmptyByteArray);
        int nibble = path.Bytes[0] >> 4;
        children[nibble] = Rlp.Encode(Keccak.Compute(leaf));
        children[nibble ^ 1] = Rlp.Encode(Keccak.Compute([1, 2, 3]));
        return (Rlp.Encode(children).Bytes, leaf);
    }
}
