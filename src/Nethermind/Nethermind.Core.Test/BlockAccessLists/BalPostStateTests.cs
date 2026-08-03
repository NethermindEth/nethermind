// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[Parallelizable(ParallelScope.All)]
public class BalPostStateTests
{
    private static readonly byte[] Code = [0x60, 0x01, 0x60, 0x02];
    private static readonly Hash256 CodeHash = new(ValueKeccak.Compute(Code));

    private static IReleaseSpec Spec(bool eip158 = true)
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip158Enabled.Returns(eip158);
        return spec;
    }

    private static ReadOnlyAccountChanges Changes(
        BalanceChange[]? balanceChanges = null,
        NonceChange[]? nonceChanges = null,
        CodeChange[]? codeChanges = null)
    {
        AccountChangesBuilder builder = Build.An.AccountChanges.WithAddress(TestItem.AddressA);
        if (balanceChanges is not null) builder.WithBalanceChanges(balanceChanges);
        if (nonceChanges is not null) builder.WithNonceChanges(nonceChanges);
        if (codeChanges is not null) builder.WithCodeChanges(codeChanges);
        return builder.TestObject;
    }

    [Test]
    public void Account_created_in_block_takes_all_fields_from_changes()
    {
        ReadOnlyAccountChanges changes = Changes(
            balanceChanges: [new BalanceChange(1, 25)],
            nonceChanges: [new NonceChange(1, 1)]);

        Account? post = BalPostState.Compute(parent: null, changes, Spec());

        Assert.That(post, Is.EqualTo(new Account(1, 25)));
    }

    [Test]
    public void Unchanged_fields_fall_back_to_parent()
    {
        Account parent = new(5, 100, TestItem.KeccakB, CodeHash);
        ReadOnlyAccountChanges changes = Changes(balanceChanges: [new BalanceChange(2, 42)]);

        Account? post = BalPostState.Compute(parent, changes, Spec());

        Assert.That(post, Is.EqualTo(new Account(5, 42, TestItem.KeccakB, CodeHash)));
    }

    [Test]
    public void Last_change_wins_per_field()
    {
        ReadOnlyAccountChanges changes = Changes(
            balanceChanges: [new BalanceChange(1, 10), new BalanceChange(3, 30)],
            nonceChanges: [new NonceChange(1, 1), new NonceChange(3, 2)]);

        Account? post = BalPostState.Compute(parent: null, changes, Spec());

        Assert.That(post, Is.EqualTo(new Account(2, 30)));
    }

    [Test]
    public void Eip158_totally_empty_post_account_is_absent()
    {
        Account parent = new(0, 100);
        ReadOnlyAccountChanges changes = Changes(balanceChanges: [new BalanceChange(1, UInt256.Zero)]);

        Account? post = BalPostState.Compute(parent, changes, Spec(eip158: true));

        Assert.That(post, Is.Null);
    }

    [Test]
    public void Pre_eip158_totally_empty_post_account_persists()
    {
        Account parent = new(0, 100);
        ReadOnlyAccountChanges changes = Changes(balanceChanges: [new BalanceChange(1, UInt256.Zero)]);

        Account? post = BalPostState.Compute(parent, changes, Spec(eip158: false));

        Assert.That(post, Is.EqualTo(new Account(0, UInt256.Zero)));
    }

    [Test]
    public void Code_change_keeps_unchanged_nonce_and_balance()
    {
        Account parent = new(7, 1);
        ReadOnlyAccountChanges changes = Changes(codeChanges: [new CodeChange(1, Code)]);

        Account? post = BalPostState.Compute(parent, changes, Spec());

        Assert.That(post, Is.EqualTo(new Account(7, 1, Keccak.EmptyTreeHash, CodeHash)));
    }

    [Test]
    public void Balance_only_change_keeps_parent_storage_root()
    {
        Account parent = new(1, 5, TestItem.KeccakC, Keccak.OfAnEmptyString);
        ReadOnlyAccountChanges changes = Changes(balanceChanges: [new BalanceChange(1, 6)]);

        Account? post = BalPostState.Compute(parent, changes, Spec());

        Assert.That(post, Is.EqualTo(new Account(1, 6, TestItem.KeccakC, Keccak.OfAnEmptyString)));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Reads_only_row_returns_parent_unchanged(bool parentExists)
    {
        Account? parent = parentExists ? new Account(0, 0) : null;
        ReadOnlyAccountChanges changes = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageReads((UInt256)1)
            .TestObject;

        Account? post = BalPostState.Compute(parent, changes, Spec());

        Assert.That(post, Is.SameAs(parent));
    }
}
