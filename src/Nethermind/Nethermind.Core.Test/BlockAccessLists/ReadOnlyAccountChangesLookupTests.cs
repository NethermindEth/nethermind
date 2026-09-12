// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the per-index lookup semantics on <see cref="ReadOnlyAccountChanges"/>:
/// <see cref="ReadOnlyAccountChanges.GetBalance"/> / <c>GetNonce</c> / <c>GetCode</c> /
/// <c>GetCodeHash</c> and the <c>TryGetLast*ChangeBefore</c> family return the last in-block
/// change strictly before the queried index, or <c>null</c> if none. The <c>null</c> is what
/// triggers <see cref="Nethermind.State.BlockAccessListBasedWorldState"/>'s fallthrough to its
/// parent-state reader.
/// </summary>
[TestFixture]
public class ReadOnlyAccountChangesLookupTests
{
    [Test]
    public void GetBalance_returns_null_when_no_changes_present()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.GetBalance(0), Is.Null);
            Assert.That(ac.GetBalance(uint.MaxValue), Is.Null);
        }
    }

    [Test]
    public void GetBalance_returns_null_when_only_change_is_at_or_after_index()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(2, 200))
            .TestObject;

        // The filter is strictly-before: a change at 2 is excluded by GetBalance(2),
        // and GetBalance(0) sees nothing at all.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.GetBalance(0), Is.Null);
            Assert.That(ac.GetBalance(2), Is.Null);
        }
    }

    [Test]
    public void GetBalance_returns_latest_change_strictly_before_index()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 100), new BalanceChange(2, 300))
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.GetBalance(1), Is.EqualTo((UInt256)100), "last change strictly before 1 is at index 0");
            Assert.That(ac.GetBalance(2), Is.EqualTo((UInt256)100), "strictly-before excludes the change at 2 itself");
            Assert.That(ac.GetBalance(3), Is.EqualTo((UInt256)300), "last change strictly before 3 is at index 2");
            Assert.That(ac.GetBalance(uint.MaxValue), Is.EqualTo((UInt256)300));
        }
    }

    [Test]
    public void GetNonce_follows_strictly_before_filter()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithNonceChanges(new NonceChange(0, 5), new NonceChange(2, 7))
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.GetNonce(0), Is.Null);
            Assert.That(ac.GetNonce(1), Is.EqualTo((UInt256)5));
            Assert.That(ac.GetNonce(2), Is.EqualTo((UInt256)5));
            Assert.That(ac.GetNonce(3), Is.EqualTo((UInt256)7));
        }
    }

    [Test]
    public void GetCode_and_GetCodeHash_return_null_when_no_prior_code_change()
    {
        // Null signals "no in-block change" so callers fall through to the parent-state reader.
        ReadOnlyAccountChanges ac = Build.An.AccountChanges.WithAddress(TestItem.AddressA).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.GetCode(0), Is.Null);
            // Regression for GetCodeHash(uint)'s ternary: without an explicit (ValueHash256?)
            // on the null branch, C# routes via Hash256?->ValueHash256's user-defined implicit
            // operator and the false branch lifts to HasValue=true.
            Assert.That(ac.GetCodeHash(0).HasValue, Is.False);
        }
    }

    [Test]
    public void GetCode_returns_latest_change_strictly_before_index()
    {
        byte[] codeAt0 = [0x60, 0x00];
        byte[] codeAt2 = [0x60, 0x01];
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithCodeChanges(new CodeChange(0, codeAt0), new CodeChange(2, codeAt2))
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.GetCode(0), Is.Null);
            Assert.That(ac.GetCode(1), Is.EqualTo(codeAt0));
            Assert.That(ac.GetCode(2), Is.EqualTo(codeAt0));
            Assert.That(ac.GetCode(3), Is.EqualTo(codeAt2));
            Assert.That(ac.GetCodeHash(3), Is.EqualTo(ValueKeccak.Compute(codeAt2)));
        }
    }

    [Test]
    public void TryGetLastBalanceChangeBefore_reports_presence_and_value_together()
    {
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(1, 99))
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.TryGetLastBalanceChangeBefore(0, out _), Is.False, "no change strictly before 0");
            Assert.That(ac.TryGetLastBalanceChangeBefore(1, out BalanceChange atOne), Is.False);
            Assert.That(atOne, Is.EqualTo(default(BalanceChange)));
            Assert.That(ac.TryGetLastBalanceChangeBefore(2, out BalanceChange atTwo), Is.True);
            Assert.That(atTwo.Index, Is.EqualTo(1u));
            Assert.That(atTwo.Value, Is.EqualTo((UInt256)99));
        }
    }

    [Test]
    public void Last_change_via_indexer_is_the_highest_indexed_change()
    {
        // ApplyStateChanges reads [^1] to apply the final state for the account, so [^1] must
        // return the highest-index in-block change.
        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithBalanceChanges(new BalanceChange(0, 100), new BalanceChange(2, 300))
            .TestObject;

        BalanceChange last = ac.BalanceChanges[^1];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Index, Is.EqualTo(2u));
            Assert.That(last.Value, Is.EqualTo((UInt256)300));
        }
    }

    /// <summary>A declared slot resolves in one probe, whether it was written or only read.</summary>
    /// <remarks>The read counts straddle the threshold at which the account switches from scanning its
    /// declared reads to mapping them, so both paths are covered.</remarks>
    [Test]
    public void TryGetDeclaredSlot_separates_changed_read_and_undeclared([Values(2, 64)] int readCount)
    {
        UInt256[] reads = new UInt256[readCount];
        for (int i = 0; i < readCount; i++)
        {
            reads[i] = (UInt256)(i + 1);
        }

        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageReads(reads)
            .WithStorageChanges((UInt256)1000, new StorageChange(0, 42))
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.TryGetDeclaredSlot((UInt256)1000, out ReadOnlySlotChanges? changed), Is.True);
            Assert.That(changed, Is.Not.Null, "a written slot carries its changes");

            Assert.That(ac.TryGetDeclaredSlot(reads[^1], out ReadOnlySlotChanges? read), Is.True);
            Assert.That(read, Is.Null, "a slot declared only as a read carries no changes");

            Assert.That(ac.TryGetDeclaredSlot((UInt256)9999, out ReadOnlySlotChanges? absent), Is.False);
            Assert.That(absent, Is.Null);
        }
    }

    [Test]
    public void TryGetSlotChanges_and_IsStorageRead_agree_with_the_declared_slot([Values(2, 64)] int readCount)
    {
        UInt256[] reads = new UInt256[readCount];
        for (int i = 0; i < readCount; i++)
        {
            reads[i] = (UInt256)(i + 1);
        }

        ReadOnlyAccountChanges ac = Build.An.AccountChanges
            .WithAddress(TestItem.AddressA)
            .WithStorageReads(reads)
            .WithStorageChanges((UInt256)1000, new StorageChange(0, 42))
            .TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ac.TryGetSlotChanges((UInt256)1000, out ReadOnlySlotChanges? changed), Is.True);
            Assert.That(changed, Is.Not.Null);
            Assert.That(ac.IsStorageRead((UInt256)1000), Is.False, "a written slot is not a read-only access");

            Assert.That(ac.TryGetSlotChanges(reads[0], out _), Is.False, "a read-only slot has no changes");
            Assert.That(ac.IsStorageRead(reads[0]), Is.True);

            Assert.That(ac.TryGetSlotChanges((UInt256)9999, out _), Is.False);
            Assert.That(ac.IsStorageRead((UInt256)9999), Is.False);
        }
    }

    /// <summary>Equality still compares the declared storage, now that reads and changes share a map.</summary>
    [Test]
    public void Equals_distinguishes_accounts_by_their_storage([Values(2, 64)] int readCount)
    {
        UInt256[] reads = new UInt256[readCount];
        for (int i = 0; i < readCount; i++)
        {
            reads[i] = (UInt256)(i + 1);
        }

        static ReadOnlyAccountChanges Make(UInt256[] reads, UInt256 changedSlot, byte value) =>
            Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(reads)
                .WithStorageChanges(changedSlot, new StorageChange(0, value))
                .TestObject;

        ReadOnlyAccountChanges subject = Make(reads, 1000, 42);
        ReadOnlyAccountChanges identical = Make(reads, 1000, 42);
        ReadOnlyAccountChanges otherValue = Make(reads, 1000, 43);
        ReadOnlyAccountChanges otherSlot = Make(reads, 1001, 42);
        ReadOnlyAccountChanges fewerReads = Make(reads[..1], 1000, 42);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(subject, Is.EqualTo(identical));
            Assert.That(subject, Is.Not.EqualTo(otherValue), "differing value");
            Assert.That(subject, Is.Not.EqualTo(otherSlot), "differing slot");
            Assert.That(subject, Is.Not.EqualTo(fewerReads), "differing reads");
        }
    }
}
