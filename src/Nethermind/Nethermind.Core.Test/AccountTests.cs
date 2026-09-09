// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class AccountTests
{
    [Test]
    public void Hashing_preserves_entropy_when_both_chained_inputs_coincide()
    {
        UInt256 balance = 2;
        // The two chained hashes collapsed only when both of their inputs matched, so the nonce has to
        // equal the seed the balance contributes as well as the code hash equalling the storage root.
        ulong nonce = (uint)balance.GetHashCode();
        HashSet<int> hashes = [];
        byte[] bytes = new byte[32];
        for (int i = 0; i < 1024; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, i);
            Hash256 root = Keccak.Compute(bytes);
            Account account = new(nonce, balance, root, root);
            hashes.Add(account.GetHashCode());
        }

        Assert.That(hashes.Count, Is.GreaterThan(1020));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Hashing_includes_each_account_field(int field)
    {
        Account original = new(1UL, 2, TestItem.KeccakA, TestItem.KeccakB);
        Account changed = field switch
        {
            0 => original.WithChangedNonce(0x100000000UL),
            1 => original.WithChangedBalance(3),
            2 => original.WithChangedStorageRoot(TestItem.KeccakC),
            _ => original.WithChangedCodeHash(TestItem.KeccakC)
        };
        Account equal = new(1UL, 2, TestItem.KeccakA, TestItem.KeccakB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(equal.GetHashCode(), Is.EqualTo(original.GetHashCode()));
            Assert.That(changed.GetHashCode(), Is.Not.EqualTo(original.GetHashCode()));
        }
    }

    [Test]
    public void Test_totally_empty()
    {
        Account account = Account.TotallyEmpty;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.IsTotallyEmpty, Is.True, "totally empty");
            Assert.That(account.IsEmpty, Is.True, "empty");
        }
    }

    [Test]
    public void Test_just_empty()
    {
        Account account = Account.TotallyEmpty;
        account = account.WithChangedStorageRoot(TestItem.KeccakA);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.IsTotallyEmpty, Is.False, "totally empty");
            Assert.That(account.IsEmpty, Is.True, "empty");
        }
    }

    [Test]
    public void Test_has_code()
    {
        Account account = Account.TotallyEmpty;
        Assert.That(account.HasCode, Is.False);
        account = account.WithChangedCodeHash(TestItem.KeccakA);
        Assert.That(account.HasCode, Is.True);
    }

    [Test]
    public void Test_has_storage()
    {
        Account account = Account.TotallyEmpty;
        Assert.That(account.HasStorage, Is.False);
        account = account.WithChangedStorageRoot(TestItem.KeccakA);
        Assert.That(account.HasStorage, Is.True);
    }
}
